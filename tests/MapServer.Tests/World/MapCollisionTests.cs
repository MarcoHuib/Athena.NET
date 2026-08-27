using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class MapCollisionTests
{
    private static MapCollisionMap MakeMap(int width, int height, MapCellFlags[] cells, string name = "unit_test_map") =>
        new(name, width, height, cells);

    [Fact]
    public void GetCell_ReturnsFlagsAtCorrectRowMajorIndex()
    {
        var map = MakeMap(2, 2,
        [
            MapCellFlags.Walkable, MapCellFlags.None,
            MapCellFlags.Shootable, MapCellFlags.Water,
        ]);

        Assert.Equal(MapCellFlags.Walkable, map.GetCell(0, 0));
        Assert.Equal(MapCellFlags.None, map.GetCell(1, 0));
        Assert.Equal(MapCellFlags.Shootable, map.GetCell(0, 1));
        Assert.Equal(MapCellFlags.Water, map.GetCell(1, 1));
    }

    // Documents/tests the pinned map_getcellp boundary distinction (map.cpp:3329-3331,
    // "NOTE: this intentionally overrides the last row and column"): rAthena's gameplay
    // traversal check treats x >= xs-1 / y >= ys-1 as always blocked, but the RAW artifact still
    // stores real terrain data for that final row/column. MapCollisionMap.IsInBounds/GetCell
    // intentionally expose the raw x < Width / y < Height range - a future CELL_CHK*-equivalent
    // consumer must apply the xs-1/ys-1 gameplay restriction itself on top of this raw data,
    // never by narrowing this type's own bounds (which would silently discard genuine imported
    // terrain bytes for every caller, not just gameplay-traversal ones).
    [Fact]
    public void RawArtifactBounds_IncludeTheFinalRowAndColumn_UnlikePinnedGameplayTraversalBounds()
    {
        var width = 3;
        var height = 3;
        var cells = new MapCellFlags[width * height];
        // Mark the final row/column walkable so the test can prove GetCell still reports their
        // REAL stored value, even though pinned map_getcellp would treat (x=width-1, *) and
        // (*, y=height-1) as always-blocked for gameplay traversal.
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                cells[x + y * width] = MapCellFlags.Walkable;
        var map = MakeMap(width, height, cells);

        var lastColumn = width - 1;
        var lastRow = height - 1;

        // Raw artifact bounds: the final row/column ARE in-bounds and readable.
        Assert.True(map.IsInBounds(lastColumn, 0));
        Assert.True(map.IsInBounds(0, lastRow));
        Assert.Equal(MapCellFlags.Walkable, map.GetCell(lastColumn, 0));
        Assert.Equal(MapCellFlags.Walkable, map.GetCell(0, lastRow));

        // Pinned rAthena gameplay-traversal bounds are STRICTLY narrower (x < Width-1, y < Height-1)
        // - a future spawn/pathfinding consumer must apply this separately; this assertion exists
        // purely to keep that documented distinction from silently drifting out of sync with the
        // type's actual raw-bounds behavior above.
        Assert.False(lastColumn < map.Width - 1);
        Assert.False(lastRow < map.Height - 1);
    }

    [Fact]
    public void Width_And_Height_PreserveFullRawArtifactDimensions()
    {
        var map = MakeMap(5, 7, new MapCellFlags[35]);

        Assert.Equal(5, map.Width);
        Assert.Equal(7, map.Height);
        Assert.True(map.IsInBounds(4, 6)); // The final row/column, per the test above.
    }

    [Fact]
    public void IsInBounds_WorksForEdgesAndOutside()
    {
        var map = MakeMap(3, 2, new MapCellFlags[6]);

        Assert.True(map.IsInBounds(0, 0));
        Assert.True(map.IsInBounds(2, 1));
        Assert.False(map.IsInBounds(-1, 0));
        Assert.False(map.IsInBounds(3, 0));
        Assert.False(map.IsInBounds(0, 2));
    }

    [Fact]
    public void GetCell_OutOfBounds_ThrowsRatherThanReturningWalkableOrBlocked()
    {
        var map = MakeMap(2, 2, new MapCellFlags[4]);

        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetCell(5, 5));
    }

    [Fact]
    public void ConvenienceAccessors_ReflectUnderlyingFlagsIndependently()
    {
        var map = MakeMap(1, 1, [MapCellFlags.Walkable | MapCellFlags.Water]);

        Assert.True(map.IsWalkable(0, 0));
        Assert.True(map.IsWater(0, 0));
        Assert.False(map.IsShootable(0, 0));
    }

    [Fact]
    public void Constructor_CellCountMismatch_ThrowsClearly()
    {
        Assert.Throws<ArgumentException>(() => new MapCollisionMap("m", 2, 2, new MapCellFlags[3]));
    }

    [Fact]
    public void Constructor_InvalidDimensions_ThrowClearly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapCollisionMap("m", 0, 2, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapCollisionMap("m", 2, 0, []));
    }

    [Fact]
    public void EmptyMapCollisionProvider_NeverResolvesAnyMap()
    {
        var provider = EmptyMapCollisionProvider.Instance;
        Assert.False(provider.TryGetMap("any_map", out _));
    }

    [Fact]
    public void MapCollisionProvider_UnknownMapIsDistinguishableFromKnownBlockedTile()
    {
        var blockedMap = MakeMap(1, 1, [MapCellFlags.None], "blocked_map");
        var provider = new MapCollisionProvider([blockedMap]);

        // Known map, blocked tile: resolves the map, tile is genuinely non-walkable.
        Assert.True(provider.TryGetMap("blocked_map", out var resolved));
        Assert.False(resolved.IsWalkable(0, 0));

        // Unknown map entirely: TryGetMap itself fails - never silently substitutes a blocked map.
        Assert.False(provider.TryGetMap("unknown_map", out _));
    }

    [Fact]
    public void MapCollisionProvider_MultipleMapsCoexist()
    {
        var mapA = MakeMap(1, 1, [MapCellFlags.Walkable], "map_a");
        var mapB = MakeMap(2, 2, [MapCellFlags.None, MapCellFlags.Water, MapCellFlags.Walkable, MapCellFlags.Shootable], "map_b");
        var provider = new MapCollisionProvider([mapA, mapB]);

        Assert.True(provider.TryGetMap("map_a", out var resolvedA));
        Assert.Equal(1, resolvedA.Width);
        Assert.True(provider.TryGetMap("map_b", out var resolvedB));
        Assert.Equal(2, resolvedB.Width);
    }

    [Fact]
    public void MapCollisionProvider_MapNameLookupIsCaseInsensitive()
    {
        var map = MakeMap(1, 1, [MapCellFlags.Walkable], "int_land03");
        var provider = new MapCollisionProvider([map]);

        Assert.True(provider.TryGetMap("INT_LAND03", out _));
    }

    // ---- Artifact reader (MapCollisionArtifact.Read) ----

    private static byte[] BuildArtifact(string mapName, int width, int height, byte[] cellBytes, bool badMagic = false)
    {
        var nameBytes = Encoding.UTF8.GetBytes(mapName);
        var buffer = new byte[4 + 4 + nameBytes.Length + 4 + 4 + cellBytes.Length];
        var offset = 0;

        (badMagic ? "XXXX"u8.ToArray() : "AMC1"u8.ToArray()).CopyTo(buffer, offset); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)nameBytes.Length); offset += 4;
        nameBytes.CopyTo(buffer, offset); offset += nameBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), width); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), height); offset += 4;
        cellBytes.CopyTo(buffer, offset);

        return buffer;
    }

    [Fact]
    public void ArtifactRead_RoundTripsMapNameDimensionsAndCellFlags()
    {
        var cellBytes = new byte[] { (byte)MapCellFlags.Walkable, (byte)MapCellFlags.None, (byte)MapCellFlags.Water, (byte)MapCellFlags.Shootable };
        var artifact = BuildArtifact("int_land03", 2, 2, cellBytes);

        var map = MapCollisionArtifact.Read(artifact);

        Assert.Equal("int_land03", map.MapName);
        Assert.Equal(2, map.Width);
        Assert.Equal(2, map.Height);
        Assert.Equal(MapCellFlags.Walkable, map.GetCell(0, 0));
        Assert.Equal(MapCellFlags.None, map.GetCell(1, 0));
        Assert.Equal(MapCellFlags.Water, map.GetCell(0, 1));
        Assert.Equal(MapCellFlags.Shootable, map.GetCell(1, 1));
    }

    [Fact]
    public void ArtifactRead_TruncatedHeader_FailsClearly()
    {
        Assert.Throws<InvalidDataException>(() => MapCollisionArtifact.Read(new byte[3]));
    }

    [Fact]
    public void ArtifactRead_TruncatedCellData_FailsClearly()
    {
        var artifact = BuildArtifact("m", 2, 2, new byte[2]); // Declares 4 cells, only supplies 2.
        Assert.Throws<InvalidDataException>(() => MapCollisionArtifact.Read(artifact));
    }

    [Fact]
    public void ArtifactRead_InvalidMagic_FailsClearly()
    {
        var artifact = BuildArtifact("m", 1, 1, [0], badMagic: true);
        Assert.Throws<InvalidDataException>(() => MapCollisionArtifact.Read(artifact));
    }

    [Fact]
    public void ArtifactRead_InvalidDimensions_FailsClearly()
    {
        var artifact = BuildArtifact("m", 0, 1, []);
        Assert.Throws<InvalidDataException>(() => MapCollisionArtifact.Read(artifact));
    }

    [Fact]
    public void ArtifactRead_CellCountMismatch_FailsClearly()
    {
        // Declares 2x2=4 cells but supplies 5 bytes of cell data.
        var artifact = BuildArtifact("m", 2, 2, new byte[5]);
        Assert.Throws<InvalidDataException>(() => MapCollisionArtifact.Read(artifact));
    }
}
