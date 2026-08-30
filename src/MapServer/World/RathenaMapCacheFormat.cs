using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Athena.Rathena.Data;

/// <summary>Shared, deterministic parser for pinned rAthena map_cache.dat files.</summary>
public static class RathenaMapCacheFormat
{
    public sealed record Entry(string Name, int Width, int Height, byte[] RawCells);
    private const int MainHeaderLength = 8;
    private const int MapNameFieldLength = 12;
    private const int MapInfoHeaderLength = 20;

    public static IReadOnlyList<Entry> ReadAll(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MainHeaderLength) throw new InvalidDataException("map_cache.dat is truncated: missing the main header.");
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (declaredSize != (uint)bytes.Length) throw new InvalidDataException($"map_cache.dat declares file_size {declaredSize} but the actual input is {bytes.Length} bytes.");
        var entries = new List<Entry>(count); var offset = MainHeaderLength;
        for (var index = 0; index < count; index++)
        {
            if (bytes.Length < offset + MapInfoHeaderLength) throw new InvalidDataException($"map_cache.dat is truncated: missing map_cache_map_info record {index} of {count}.");
            var nameField = bytes.Slice(offset, MapNameFieldLength); var nul = nameField.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(nul < 0 ? nameField : nameField[..nul]);
            var width = BinaryPrimitives.ReadInt16LittleEndian(bytes[(offset + 12)..]);
            var height = BinaryPrimitives.ReadInt16LittleEndian(bytes[(offset + 14)..]);
            var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 16)..]);
            var payload = offset + MapInfoHeaderLength;
            if (width <= 0 || height <= 0) throw new InvalidDataException($"map_cache.dat record {index} ('{name}') has invalid dimensions ({width}x{height}).");
            if (compressedLength < 0 || bytes.Length < payload + compressedLength) throw new InvalidDataException($"map_cache.dat record {index} ('{name}') has an invalid compressed length {compressedLength}.");
            var expected = checked(width * height);
            using var input = new MemoryStream(bytes.Slice(payload, compressedLength).ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress); using var output = new MemoryStream(expected);
            zlib.CopyTo(output); var cells = output.ToArray();
            if (cells.Length != expected) throw new InvalidDataException($"map_cache.dat record '{name}' decompressed to {cells.Length} bytes, expected {expected}.");
            if (cells.Any(cell => cell > 6)) throw new InvalidDataException($"map_cache.dat map '{name}' contains an unrecognized GAT cell type.");
            entries.Add(new(name, width, height, cells)); offset = payload + compressedLength;
        }
        return entries;
    }
}
