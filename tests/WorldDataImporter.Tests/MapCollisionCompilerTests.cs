using System.Buffers.Binary;
using Athena.WorldCompiler.Generation;

// Uses only tiny synthetic .gat-shaped byte fixtures built in-test - never real Gravity client
// map bytes (see ai/world-data.md's licensing rule). BuildGat below reproduces exactly the layout
// MapCollisionCompiler's own doc comment traces from pinned mapcache.cpp: 6-byte signature,
// width/height (uint32 LE) at offsets 6/10, then one 20-byte record per cell (16 bytes of unused
// height floats + a uint32 LE GAT type at the record's +16 offset).
public sealed class MapCollisionCompilerTests
{
    private static byte[] BuildGat(int width, int height, uint[] cellTypes, bool truncate = false, bool badSignature = false)
    {
        var headerLength = 14;
        var recordLength = 20;
        var buffer = new byte[headerLength + width * height * recordLength];

        byte[] signature = badSignature ? [0x00, 0x00, 0x00, 0x00, 0x00, 0x00] : [0x47, 0x52, 0x41, 0x54, 0x01, 0x02];
        signature.CopyTo(buffer, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10, 4), (uint)height);

        for (var i = 0; i < cellTypes.Length; i++)
        {
            var recordOffset = headerLength + i * recordLength;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(recordOffset + 16, 4), cellTypes[i]);
        }

        return truncate ? buffer[..^5] : buffer;
    }

    [Fact]
    public void Compile_ValidMinimalFixture_ProducesCorrectWidthAndHeight()
    {
        var gat = BuildGat(2, 3, [0, 0, 0, 0, 0, 0]);
        var map = MapCollisionCompiler.Compile(gat, "unit_test_map");

        Assert.Equal("unit_test_map", map.MapName);
        Assert.Equal(2, map.Width);
        Assert.Equal(3, map.Height);
    }

    [Fact]
    public void Compile_CellCountMatchesWidthTimesHeight()
    {
        var gat = BuildGat(4, 5, Enumerable.Repeat(0u, 20).ToArray());
        var map = MapCollisionCompiler.Compile(gat, "m");

        Assert.Equal(20, map.Cells.Length);
    }

    [Theory]
    [InlineData(0u, CompiledMapCellFlags.Walkable | CompiledMapCellFlags.Shootable)]
    [InlineData(1u, CompiledMapCellFlags.None)]
    [InlineData(3u, CompiledMapCellFlags.Walkable | CompiledMapCellFlags.Shootable | CompiledMapCellFlags.Water)]
    [InlineData(5u, CompiledMapCellFlags.Shootable)]
    public void Compile_GatTypeMapsToExpectedFlags(uint gatType, CompiledMapCellFlags expected)
    {
        var gat = BuildGat(1, 1, [gatType]);
        var map = MapCollisionCompiler.Compile(gat, "m");

        Assert.Equal(expected, map.Cells[0]);
    }

    [Fact]
    public void Compile_WallCellIsDistinguishableFromWalkableCell()
    {
        var gat = BuildGat(2, 1, [0, 1]);
        var map = MapCollisionCompiler.Compile(gat, "m");

        Assert.True(map.Cells[0].HasFlag(CompiledMapCellFlags.Walkable));
        Assert.False(map.Cells[1].HasFlag(CompiledMapCellFlags.Walkable));
    }

    [Fact]
    public void Compile_WaterSemanticsAreIndependentOfWalkability()
    {
        // Type 3 (walkable water) must carry both Walkable and Water; a plain walkable cell (type
        // 0) must not carry Water even though both are walkable.
        var gat = BuildGat(2, 1, [0, 3]);
        var map = MapCollisionCompiler.Compile(gat, "m");

        Assert.True(map.Cells[0].HasFlag(CompiledMapCellFlags.Walkable));
        Assert.False(map.Cells[0].HasFlag(CompiledMapCellFlags.Water));
        Assert.True(map.Cells[1].HasFlag(CompiledMapCellFlags.Walkable));
        Assert.True(map.Cells[1].HasFlag(CompiledMapCellFlags.Water));
    }

    [Fact]
    public void Compile_ShootabilityIsIndependentOfWalkability()
    {
        // Type 5 (gap/cliff) is shootable but not walkable - the one GAT type where the two static
        // flags genuinely diverge, proving shootable isn't silently derived as "== walkable".
        var gat = BuildGat(2, 1, [1, 5]);
        var map = MapCollisionCompiler.Compile(gat, "m");

        Assert.False(map.Cells[0].HasFlag(CompiledMapCellFlags.Shootable));
        Assert.False(map.Cells[1].HasFlag(CompiledMapCellFlags.Walkable));
        Assert.True(map.Cells[1].HasFlag(CompiledMapCellFlags.Shootable));
    }

    [Fact]
    public void Compile_TruncatedInput_FailsClearly()
    {
        var gat = BuildGat(2, 2, [0, 0, 0, 0], truncate: true);
        Assert.Throws<InvalidDataException>(() => MapCollisionCompiler.Compile(gat, "m"));
    }

    [Fact]
    public void Compile_HeaderOnlyInput_FailsClearly()
    {
        Assert.Throws<InvalidDataException>(() => MapCollisionCompiler.Compile(new byte[10], "m"));
    }

    [Fact]
    public void Compile_InvalidSignature_FailsClearly()
    {
        var gat = BuildGat(1, 1, [0], badSignature: true);
        Assert.Throws<InvalidDataException>(() => MapCollisionCompiler.Compile(gat, "m"));
    }

    [Fact]
    public void Compile_ZeroWidth_FailsClearly()
    {
        var gat = BuildGat(0, 1, []);
        Assert.Throws<InvalidDataException>(() => MapCollisionCompiler.Compile(gat, "m"));
    }

    [Fact]
    public void Compile_UnrecognizedGatType_FailsClearly()
    {
        var gat = BuildGat(1, 1, [99]);
        Assert.Throws<InvalidDataException>(() => MapCollisionCompiler.Compile(gat, "m"));
    }

    [Fact]
    public void MapCollisionRoundTrip_MatchesRuntimeReader()
    {
        // Decodes MapCollisionArtifactWriter's own output using the EXACT layout
        // Athena.Net.MapServer.World.MapCollisionArtifact.Read expects, byte-for-byte, without
        // referencing the MapServer project (WorldDataImporter has no dependency on it) - this is
        // what keeps the two independently-maintained format implementations in sync.
        var gat = BuildGat(2, 2, [0, 1, 3, 5]);
        var compiled = MapCollisionCompiler.Compile(gat, "roundtrip_map");
        var artifact = MapCollisionArtifactWriter.Write(compiled);

        Assert.Equal((byte)'A', artifact[0]);
        Assert.Equal((byte)'M', artifact[1]);
        Assert.Equal((byte)'C', artifact[2]);
        Assert.Equal((byte)'1', artifact[3]);

        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(artifact.AsSpan(4, 4));
        Assert.Equal((uint)"roundtrip_map".Length, nameLength);

        var offset = 8;
        var name = System.Text.Encoding.UTF8.GetString(artifact, offset, (int)nameLength); offset += (int)nameLength;
        Assert.Equal("roundtrip_map", name);

        var width = BinaryPrimitives.ReadInt32LittleEndian(artifact.AsSpan(offset, 4)); offset += 4;
        var height = BinaryPrimitives.ReadInt32LittleEndian(artifact.AsSpan(offset, 4)); offset += 4;
        Assert.Equal(2, width);
        Assert.Equal(2, height);

        var cellBytes = artifact[offset..];
        Assert.Equal(4, cellBytes.Length);
        Assert.Equal((byte)compiled.Cells[0], cellBytes[0]);
        Assert.Equal((byte)compiled.Cells[1], cellBytes[1]);
        Assert.Equal((byte)compiled.Cells[2], cellBytes[2]);
        Assert.Equal((byte)compiled.Cells[3], cellBytes[3]);
    }

    [Fact]
    public void Write_IsByteForByteDeterministic()
    {
        var gat = BuildGat(3, 3, Enumerable.Range(0, 9).Select(i => (uint)(i % 2 == 0 ? 0 : 1)).ToArray());
        var compiled1 = MapCollisionCompiler.Compile(gat, "deterministic_map");
        var compiled2 = MapCollisionCompiler.Compile(gat, "deterministic_map");

        var artifact1 = MapCollisionArtifactWriter.Write(compiled1);
        var artifact2 = MapCollisionArtifactWriter.Write(compiled2);

        Assert.Equal(artifact1, artifact2);
    }
}
