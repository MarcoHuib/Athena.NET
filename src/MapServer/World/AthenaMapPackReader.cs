using Athena.MapPacks;
using Microsoft.Win32.SafeHandles;

namespace Athena.Net.MapServer.World;

public sealed class AthenaMapPackReader : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly AthenaMapPackFormat.IndexEntry[] _index;
    private readonly long _fileLength;
    public int MapCount => _index.Length;

    public AthenaMapPackReader(string path, int expectedMapCount)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            _fileLength = RandomAccess.GetLength(_handle);
            Span<byte> headerBytes = stackalloc byte[AthenaMapPackFormat.HeaderSize]; ReadExactly(headerBytes, 0, "header");
            var header = AthenaMapPackFormat.ReadHeader(headerBytes);
            if (header.MapCount != expectedMapCount) throw new InvalidDataException($"Athena Map Pack contains {header.MapCount} maps; expected {expectedMapCount}.");
            var indexLength = checked((long)header.MapCount * AthenaMapPackFormat.IndexEntrySize);
            if (header.IndexOffset != AthenaMapPackFormat.HeaderSize || header.PayloadOffset != checked((ulong)AthenaMapPackFormat.HeaderSize + (ulong)indexLength) || header.PayloadOffset > (ulong)_fileLength)
                throw new InvalidDataException("Athena Map Pack index/payload offsets are invalid.");
            var indexBytes = new byte[indexLength]; ReadExactly(indexBytes, checked((long)header.IndexOffset), "index");
            _index = new AthenaMapPackFormat.IndexEntry[header.MapCount]; ulong priorEnd = header.PayloadOffset;
            for (var assetId = 0; assetId < _index.Length; assetId++)
            {
                var entry = AthenaMapPackFormat.ReadIndexEntry(indexBytes.AsSpan(assetId * AthenaMapPackFormat.IndexEntrySize, AthenaMapPackFormat.IndexEntrySize));
                ValidateEntry(entry, assetId, header.PayloadOffset, priorEnd); _index[assetId] = entry;
                priorEnd = checked(entry.PayloadOffset + entry.PayloadLength);
            }
            if (priorEnd != (ulong)_fileLength) throw new InvalidDataException("Athena Map Pack contains trailing bytes or a truncated final payload.");
        }
        catch { _handle.Dispose(); throw; }
    }

    public MapCollisionMap ReadMap(GeneratedMapDefinition definition)
    {
        if ((uint)definition.AssetId >= (uint)_index.Length) throw new InvalidDataException($"Map '{definition.Name}' has out-of-range AssetId {definition.AssetId}.");
        var entry = _index[definition.AssetId];
        if (entry.Width != definition.Width || entry.Height != definition.Height) throw new InvalidDataException($"Map '{definition.Name}' metadata dimensions disagree with pack asset {definition.AssetId}.");
        var packed = new byte[entry.PayloadLength]; ReadExactly(packed, checked((long)entry.PayloadOffset), $"payload for map '{definition.Name}' asset {definition.AssetId}");
        if ((entry.CellCount & 1) != 0 && (packed[^1] & 0xF0) != 0) throw new InvalidDataException($"Map '{definition.Name}' has a non-zero unused Packed4 high nibble.");
        var cells = new MapCellFlags[entry.CellCount];
        for (var index = 0; index < cells.Length; index++)
        {
            var value = (byte)(((index & 1) == 0 ? packed[index >> 1] : packed[index >> 1] >> 4) & 0x0F);
            cells[index] = value switch
            {
                0 or 2 or 4 or 6 => MapCellFlags.Walkable | MapCellFlags.Shootable,
                1 => MapCellFlags.None,
                3 => MapCellFlags.Walkable | MapCellFlags.Shootable | MapCellFlags.Water,
                5 => MapCellFlags.Shootable,
                _ => throw new InvalidDataException($"Map '{definition.Name}' asset {definition.AssetId} cell {index} has unsupported Packed4 GAT value {value}."),
            };
        }
        return new(definition.Name, definition.Width, definition.Height, cells);
    }

    private void ValidateEntry(AthenaMapPackFormat.IndexEntry entry, int assetId, ulong payloadArea, ulong priorEnd)
    {
        if (entry.Encoding != AthenaMapPackFormat.Packed4Encoding) throw new InvalidDataException($"Athena Map Pack asset {assetId} uses unsupported encoding {entry.Encoding}.");
        if (entry.Width == 0 || entry.Height == 0 || entry.CellCount != checked((uint)entry.Width * entry.Height)) throw new InvalidDataException($"Athena Map Pack asset {assetId} has inconsistent width, height, or cell count.");
        if (entry.PayloadLength != (entry.CellCount + 1) / 2) throw new InvalidDataException($"Athena Map Pack asset {assetId} Packed4 payload length is invalid.");
        ulong end; try { end = checked(entry.PayloadOffset + entry.PayloadLength); } catch (OverflowException ex) { throw new InvalidDataException($"Athena Map Pack asset {assetId} payload arithmetic overflowed.", ex); }
        if (entry.PayloadOffset < payloadArea || entry.PayloadOffset != priorEnd || end > (ulong)_fileLength) throw new InvalidDataException($"Athena Map Pack asset {assetId} payload bounds overlap, contain gaps, or fall outside the file.");
    }

    private void ReadExactly(Span<byte> destination, long offset, string context)
    {
        var read = 0;
        while (read < destination.Length) { var count = RandomAccess.Read(_handle, destination[read..], checked(offset + read)); if (count == 0) throw new InvalidDataException($"Athena Map Pack is truncated while reading {context}."); read += count; }
    }
    public void Dispose() => _handle.Dispose();
}
