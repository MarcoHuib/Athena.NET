using System.Buffers.Binary;

namespace Athena.MapPacks;

public static class AthenaMapPackFormat
{
    public const ushort Version = 1;
    public const int HeaderSize = 32;
    public const int IndexEntrySize = 24;
    public const byte Packed4Encoding = 1;
    public static ReadOnlySpan<byte> Magic => "ATHMAP\0\0"u8;
    public readonly record struct Header(ushort FormatVersion, uint MapCount, ulong IndexOffset, ulong PayloadOffset);
    public readonly record struct IndexEntry(ulong PayloadOffset, uint PayloadLength, uint CellCount, ushort Width, ushort Height, byte Encoding);

    public static void WriteHeader(Span<byte> destination, uint mapCount, ulong payloadOffset)
    {
        if (destination.Length < HeaderSize) throw new ArgumentException("Athena Map Pack header destination is too small.", nameof(destination));
        destination[..HeaderSize].Clear(); Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], mapCount);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], payloadOffset);
    }

    public static Header ReadHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize) throw new InvalidDataException("Athena Map Pack is truncated before its complete header.");
        if (!source[..Magic.Length].SequenceEqual(Magic)) throw new InvalidDataException("Athena Map Pack magic is invalid.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]);
        if (version != Version) throw new InvalidDataException($"Athena Map Pack version {version} is unsupported; expected {Version}.");
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(source[10..]);
        if (headerSize != HeaderSize) throw new InvalidDataException($"Athena Map Pack header size {headerSize} is invalid; expected {HeaderSize}.");
        return new(version, BinaryPrimitives.ReadUInt32LittleEndian(source[12..]), BinaryPrimitives.ReadUInt64LittleEndian(source[16..]), BinaryPrimitives.ReadUInt64LittleEndian(source[24..]));
    }

    public static void WriteIndexEntry(Span<byte> destination, IndexEntry entry)
    {
        if (destination.Length < IndexEntrySize) throw new ArgumentException("Athena Map Pack index destination is too small.", nameof(destination));
        destination[..IndexEntrySize].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(destination, entry.PayloadOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], entry.PayloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], entry.CellCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..], entry.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[18..], entry.Height);
        destination[20] = entry.Encoding;
    }

    public static IndexEntry ReadIndexEntry(ReadOnlySpan<byte> source)
    {
        if (source.Length < IndexEntrySize) throw new InvalidDataException("Athena Map Pack index entry is truncated.");
        if (source[21] != 0 || source[22] != 0 || source[23] != 0) throw new InvalidDataException("Athena Map Pack index reserved bytes must be zero.");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(source), BinaryPrimitives.ReadUInt32LittleEndian(source[8..]), BinaryPrimitives.ReadUInt32LittleEndian(source[12..]), BinaryPrimitives.ReadUInt16LittleEndian(source[16..]), BinaryPrimitives.ReadUInt16LittleEndian(source[18..]), source[20]);
    }

    public static byte[] Pack4(ReadOnlySpan<byte> cells)
    {
        var packed = new byte[(cells.Length + 1) / 2];
        for (var index = 0; index < cells.Length; index++)
        {
            var value = cells[index];
            if (value > 6) throw new InvalidDataException($"GAT cell {index} has unsupported value {value}.");
            if ((index & 1) == 0) packed[index >> 1] = value; else packed[index >> 1] |= (byte)(value << 4);
        }
        return packed;
    }

    public static byte[] Unpack4(ReadOnlySpan<byte> packed, int cellCount)
    {
        if (packed.Length != (cellCount + 1) / 2) throw new InvalidDataException("Packed4 payload length does not match its cell count.");
        if ((cellCount & 1) != 0 && (packed[^1] & 0xF0) != 0) throw new InvalidDataException("Packed4 odd-cell payload has a non-zero unused high nibble.");
        var cells = new byte[cellCount];
        for (var index = 0; index < cells.Length; index++)
        {
            var value = (byte)(((index & 1) == 0 ? packed[index >> 1] : packed[index >> 1] >> 4) & 0x0F);
            if (value > 6) throw new InvalidDataException($"Packed4 cell {index} has unsupported GAT value {value}.");
            cells[index] = value;
        }
        return cells;
    }
}
