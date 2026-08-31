using Athena.MapPacks;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class AthenaMapPackReaderTests
{
    [Theory]
    [InlineData(new byte[] { 0, 1, 2, 3 })]
    [InlineData(new byte[] { 6, 5, 4 })]
    public void Packed4_RoundTripsEvenAndOddCellCounts(byte[] cells) => Assert.Equal(cells, AthenaMapPackFormat.Unpack4(AthenaMapPackFormat.Pack4(cells), cells.Length));

    [Fact]
    public void Reader_MultipleBlocksArePositionallyIndependent_AndProviderCaches()
    {
        var path = WritePack(([0, 1, 2, 3], 2, 2), ([6, 5, 4], 3, 1));
        try
        {
            using var reader = new AthenaMapPackReader(path, 2);
            var definitions = new[] { Definition(0, "first", 2, 2), Definition(1, "second", 3, 1) };
            using var provider = new GeneratedMapCollisionProvider(definitions, reader);
            Assert.True(provider.TryGetMap("SECOND.GAT", out var second)); Assert.True(second.IsShootable(1, 0));
            Assert.True(provider.TryGetMap("second", out var cached)); Assert.Same(second, cached);
            Assert.False(provider.TryGetMap("missing", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_FailsClosedForBadMagicVersionAndUnsupportedEncoding()
    {
        var path = WritePack(([0, 1], 2, 1));
        try
        {
            Mutate(path, 0, 0xFF); Assert.Throws<InvalidDataException>(() => new AthenaMapPackReader(path, 1));
            File.Delete(path); path = WritePack(([0, 1], 2, 1)); Mutate(path, 8, 2); Assert.Throws<InvalidDataException>(() => new AthenaMapPackReader(path, 1));
            File.Delete(path); path = WritePack(([0, 1], 2, 1)); Mutate(path, AthenaMapPackFormat.HeaderSize + 20, 99); Assert.Throws<InvalidDataException>(() => new AthenaMapPackReader(path, 1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_FailsClosedForInvalidOffsetCellCountTruncationAndMetadataDimensions()
    {
        var path = WritePack(([0, 1, 2, 3], 2, 2));
        try
        {
            Mutate(path, AthenaMapPackFormat.HeaderSize, 0); Assert.Throws<InvalidDataException>(() => new AthenaMapPackReader(path, 1));
            File.Delete(path); path = WritePack(([0, 1, 2, 3], 2, 2)); Mutate(path, AthenaMapPackFormat.HeaderSize + 12, 3); Assert.Throws<InvalidDataException>(() => new AthenaMapPackReader(path, 1));
            File.Delete(path); path = WritePack(([0, 1, 2, 3], 2, 2)); using (var stream = new FileStream(path, FileMode.Open)) stream.SetLength(stream.Length - 1); Assert.Throws<InvalidDataException>(() => new AthenaMapPackReader(path, 1));
            File.Delete(path); path = WritePack(([0, 1, 2, 3], 2, 2)); using var reader = new AthenaMapPackReader(path, 1); Assert.Throws<InvalidDataException>(() => reader.ReadMap(Definition(0, "bad", 4, 1)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_RejectsInvalidGatNibbleAndNonCanonicalOddNibble()
    {
        var path = WritePack(([0, 1], 2, 1));
        try
        {
            Mutate(path, AthenaMapPackFormat.HeaderSize + AthenaMapPackFormat.IndexEntrySize, 0x70); using (var reader = new AthenaMapPackReader(path, 1)) Assert.Throws<InvalidDataException>(() => reader.ReadMap(Definition(0, "bad", 2, 1)));
            File.Delete(path); path = WritePack(([0], 1, 1)); Mutate(path, AthenaMapPackFormat.HeaderSize + AthenaMapPackFormat.IndexEntrySize, 0x10); using var odd = new AthenaMapPackReader(path, 1); Assert.Throws<InvalidDataException>(() => odd.ReadMap(Definition(0, "odd", 1, 1)));
        }
        finally { File.Delete(path); }
    }

    private static GeneratedMapDefinition Definition(int id, string name, int width, int height) => new(id, name, width, height, MapSourceLayer.Base, new("test", "test", "test", 0));
    private static string WritePack(params (byte[] Cells, ushort Width, ushort Height)[] maps)
    {
        var path = Path.Combine(Path.GetTempPath(), $"athena-map-pack-{Guid.NewGuid():N}.bin"); var payloads = maps.Select(map => AthenaMapPackFormat.Pack4(map.Cells)).ToArray();
        var payloadOffset = (ulong)(AthenaMapPackFormat.HeaderSize + maps.Length * AthenaMapPackFormat.IndexEntrySize); var bytes = new byte[checked((int)payloadOffset + payloads.Sum(item => item.Length))];
        AthenaMapPackFormat.WriteHeader(bytes, (uint)maps.Length, payloadOffset); var offset = payloadOffset;
        for (var id = 0; id < maps.Length; id++) { var entry = new AthenaMapPackFormat.IndexEntry(offset, (uint)payloads[id].Length, (uint)maps[id].Cells.Length, maps[id].Width, maps[id].Height, AthenaMapPackFormat.Packed4Encoding); AthenaMapPackFormat.WriteIndexEntry(bytes.AsSpan(AthenaMapPackFormat.HeaderSize + id * AthenaMapPackFormat.IndexEntrySize), entry); payloads[id].CopyTo(bytes, (int)offset); offset += (uint)payloads[id].Length; }
        File.WriteAllBytes(path, bytes); return path;
    }
    private static void Mutate(string path, int offset, byte value) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Write); stream.Position = offset; stream.WriteByte(value); }
}
