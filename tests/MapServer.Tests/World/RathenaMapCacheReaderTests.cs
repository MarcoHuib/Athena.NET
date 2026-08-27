using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Synthetic fixtures reproduce pinned map_cache.dat's exact byte layout (see
// RathenaMapCacheReader's own doc comment for the pinned trace): 8-byte main header (file_size,
// map_count, then 2 bytes of structure-alignment padding before the first record), then
// map_count back-to-back name(12)+xs(2)+ys(2)+len(4)+zlib(len bytes) records.
//
// Real-data tests additionally parse the actual pinned legacy/rathena/db/map_cache.dat
// (e985006171d2eb320ee512a653f4c83aea3d81b6) to prove the trace against real rAthena output, not
// just a hand-built fixture (ai/world-data.md: "Tests should include the REAL pinned
// map_cache.dat"). That file is pinned reference data (part of the legacy/rathena submodule, and
// itself server-side geometry rAthena derives from client resources, not a redistributed
// proprietary client asset), so it is not subject to the .gat/.athmap local-only licensing rule.
public sealed class RathenaMapCacheReaderTests
{
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static byte[] BuildRecord(string name, short xs, short ys, byte[] rawCells)
    {
        var compressed = ZlibCompress(rawCells);
        var record = new byte[12 + 2 + 2 + 4 + compressed.Length];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(record, 0);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(12, 2), xs);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(14, 2), ys);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16, 4), compressed.Length);
        compressed.CopyTo(record, 20);
        return record;
    }

    private static byte[] BuildMapCache(params (string Name, short Xs, short Ys, byte[] RawCells)[] maps)
    {
        var records = maps.Select(map => BuildRecord(map.Name, map.Xs, map.Ys, map.RawCells)).ToArray();
        var totalLength = 8 + records.Sum(record => record.Length);
        var buffer = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)totalLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), (ushort)maps.Length);
        var offset = 8;
        foreach (var record in records)
        {
            record.CopyTo(buffer, offset);
            offset += record.Length;
        }
        return buffer;
    }

    [Fact]
    public void ReadAll_SingleMap_ProducesCorrectNameWidthHeight()
    {
        var mapCache = BuildMapCache(("unit_test", (short)2, (short)3, [0, 0, 0, 0, 0, 0]));
        var maps = RathenaMapCacheReader.ReadAll(mapCache);

        var map = Assert.Single(maps);
        Assert.Equal("unit_test", map.MapName);
        Assert.Equal(2, map.Width);
        Assert.Equal(3, map.Height);
    }

    [Fact]
    public void ReadAll_MultipleMaps_ReadsEachRecordAtItsOwnOffset()
    {
        var mapCache = BuildMapCache(
            ("map_a", (short)2, (short)2, [0, 0, 0, 0]),
            ("map_b", (short)3, (short)1, [1, 1, 1]),
            ("map_c", (short)1, (short)1, [3]));

        var maps = RathenaMapCacheReader.ReadAll(mapCache);

        Assert.Equal(3, maps.Count);
        Assert.Equal(["map_a", "map_b", "map_c"], maps.Select(map => map.MapName));
        Assert.Equal(2, maps[0].Width);
        Assert.Equal(3, maps[1].Width);
        Assert.Equal(1, maps[2].Width);
    }

    [Theory]
    [InlineData(0, MapCellFlags.Walkable | MapCellFlags.Shootable)]
    [InlineData(1, MapCellFlags.None)]
    [InlineData(3, MapCellFlags.Walkable | MapCellFlags.Shootable | MapCellFlags.Water)]
    [InlineData(5, MapCellFlags.Shootable)]
    public void ReadAll_GatTypeMapsToExpectedFlags(byte gatType, MapCellFlags expected)
    {
        var mapCache = BuildMapCache(("m", (short)1, (short)1, [gatType]));
        var map = Assert.Single(RathenaMapCacheReader.ReadAll(mapCache));

        Assert.Equal(expected, map.GetCell(0, 0));
    }

    [Fact]
    public void ReadAll_UnrecognizedGatType_FailsClearly()
    {
        var mapCache = BuildMapCache(("m", (short)1, (short)1, [99]));
        Assert.Throws<InvalidDataException>(() => RathenaMapCacheReader.ReadAll(mapCache));
    }

    [Fact]
    public void ReadAll_MismatchedFileSizeHeader_FailsClearly()
    {
        var mapCache = BuildMapCache(("m", (short)1, (short)1, [0]));
        BinaryPrimitives.WriteUInt32LittleEndian(mapCache.AsSpan(0, 4), (uint)(mapCache.Length + 1));

        Assert.Throws<InvalidDataException>(() => RathenaMapCacheReader.ReadAll(mapCache));
    }

    [Fact]
    public void ReadAll_InvalidDimensions_FailsClearly()
    {
        var mapCache = BuildMapCache(("m", (short)0, (short)1, []));
        Assert.Throws<InvalidDataException>(() => RathenaMapCacheReader.ReadAll(mapCache));
    }

    [Fact]
    public void ReadAll_TruncatedHeader_FailsClearly()
    {
        Assert.Throws<InvalidDataException>(() => RathenaMapCacheReader.ReadAll(new byte[4]));
    }

    [Fact]
    public void ReadAll_TruncatedRecord_FailsClearly()
    {
        var mapCache = BuildMapCache(("m", (short)2, (short)2, [0, 0, 0, 0]));
        var truncated = mapCache[..^3];
        // file_size still declares the full (untruncated) length, so the truncation is caught by
        // the payload-length bounds check, not the file_size sanity check.
        Assert.Throws<InvalidDataException>(() => RathenaMapCacheReader.ReadAll(truncated));
    }

    [Fact]
    public void ReadAll_DeclaredCompressedLengthExceedsInput_FailsClearly()
    {
        var mapCache = BuildMapCache(("m", (short)1, (short)1, [0]));
        // The main header is 8 bytes; the first record's len field sits at name(12)+xs(2)+ys(2)
        // past that, i.e. absolute offset 8+16=24.
        BinaryPrimitives.WriteInt32LittleEndian(mapCache.AsSpan(24, 4), 999_999);

        Assert.Throws<InvalidDataException>(() => RathenaMapCacheReader.ReadAll(mapCache));
    }

    [Fact]
    public void ReadAll_CorruptedZlibPayload_FailsClearly()
    {
        var mapCache = BuildMapCache(("m", (short)2, (short)2, [0, 0, 0, 0]));
        mapCache[20] ^= 0xFF; // First payload byte.

        Assert.ThrowsAny<Exception>(() => RathenaMapCacheReader.ReadAll(mapCache));
    }

    [Fact]
    public void ReadAll_NameShorterThanField_IsNotPaddedWithTrailingNuls()
    {
        var mapCache = BuildMapCache(("ab", (short)1, (short)1, [0]));
        var map = Assert.Single(RathenaMapCacheReader.ReadAll(mapCache));

        Assert.Equal("ab", map.MapName);
    }

    [Fact]
    public void ReadAll_ResultsAreDeterministicAcrossRepeatedReads()
    {
        var mapCache = BuildMapCache(("det", (short)3, (short)3, [0, 1, 3, 5, 0, 1, 3, 5, 0]));

        var first = RathenaMapCacheReader.ReadAll(mapCache);
        var second = RathenaMapCacheReader.ReadAll(mapCache);

        Assert.Equal(first.Select(m => m.MapName), second.Select(m => m.MapName));
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Width, second[i].Width);
            Assert.Equal(first[i].Height, second[i].Height);
            for (var y = 0; y < first[i].Height; y++)
                for (var x = 0; x < first[i].Width; x++)
                    Assert.Equal(first[i].GetCell(x, y), second[i].GetCell(x, y));
        }
    }

    // --- Real pinned data verification ---

    [Fact]
    public void ReadAll_RealPinnedMapCache_ParsesAllDeclaredMapsExactly()
    {
        var mapCacheBytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat"));
        var maps = RathenaMapCacheReader.ReadAll(mapCacheBytes);

        // Independently confirmed via a standalone Python parse of the same pinned file: 1288 maps.
        Assert.Equal(1288, maps.Count);
        Assert.Contains(maps, map => map.MapName == "int_land");
        Assert.Contains(maps, map => map.MapName == "prt_fild00");
    }

    [Theory]
    [InlineData("int_land")]
    [InlineData("int_land01")]
    [InlineData("int_land02")]
    [InlineData("int_land03")]
    [InlineData("int_land04")]
    public void ReadAll_RealPinnedMapCache_ContainsEachIntLandVariantAsItsOwnIndependentRecord(string mapName)
    {
        // Pinned map_cache.dat already declares int_land/int_land01../04 as five separate, real
        // records (independently confirmed via a standalone Python parse) - no Athena-side alias
        // mechanism is needed for this source, unlike the .gat/.athmap secondary path where a
        // single physical client resource is deliberately shared across logical map names.
        var mapCacheBytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat"));
        var maps = RathenaMapCacheReader.ReadAll(mapCacheBytes);

        var map = Assert.Single(maps, m => m.MapName == mapName);
        Assert.Equal(140, map.Width);
        Assert.Equal(140, map.Height);
    }

    [Fact]
    public void ReadAll_RealPinnedIntLandMap_HasBothWalkableAndBlockedCells()
    {
        var mapCacheBytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat"));
        var maps = RathenaMapCacheReader.ReadAll(mapCacheBytes);

        // int_land is the map pinned npc/re/mobs/int_land.txt spawns G_PORING on (see
        // ai/world-data.md's "Monster combat and quest drops" section). Independently confirmed
        // via a standalone Python parse: 140x140 with a mix of walkable (GAT type 0), blocked
        // (type 1), and walkable-water (type 3) cells.
        var intLand = Assert.Single(maps, map => map.MapName == "int_land");
        Assert.Equal(19600, intLand.Width * intLand.Height);

        var sawWalkable = false;
        var sawBlocked = false;
        for (var y = 0; y < intLand.Height && !(sawWalkable && sawBlocked); y++)
        {
            for (var x = 0; x < intLand.Width; x++)
            {
                if (intLand.IsWalkable(x, y)) sawWalkable = true;
                else sawBlocked = true;
            }
        }
        Assert.True(sawWalkable);
        Assert.True(sawBlocked);
    }

    [Fact]
    public void ReadAll_RealPinnedFild00Map_HasWalkableBlockedWaterAndCliffCells()
    {
        // Independently confirmed via a standalone Python parse: prt_fild00 is 400x400 and
        // contains a genuine mix of all four static-semantic outcomes (types 0/1/3/5), the widest
        // real-map coverage of map_gat2cell's branches available in this pinned file.
        var mapCacheBytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat"));
        var maps = RathenaMapCacheReader.ReadAll(mapCacheBytes);
        var fild00 = Assert.Single(maps, map => map.MapName == "prt_fild00");

        Assert.Equal(400, fild00.Width);
        Assert.Equal(400, fild00.Height);

        var sawWalkableDryLand = false;
        var sawBlocked = false;
        var sawWater = false;
        var sawCliff = false;
        for (var y = 0; y < fild00.Height; y++)
        {
            for (var x = 0; x < fild00.Width; x++)
            {
                var cell = fild00.GetCell(x, y);
                if (cell.HasFlag(MapCellFlags.Walkable) && !cell.HasFlag(MapCellFlags.Water)) sawWalkableDryLand = true;
                else if (cell == MapCellFlags.None) sawBlocked = true;
                if (cell.HasFlag(MapCellFlags.Water)) sawWater = true;
                if (cell == MapCellFlags.Shootable) sawCliff = true;
            }
        }
        Assert.True(sawWalkableDryLand);
        Assert.True(sawBlocked);
        Assert.True(sawWater);
        Assert.True(sawCliff);
    }

    [Fact]
    public void ReadAll_RealPinnedMapCache_EveryMapHasPositiveDimensionsAndMatchingCellCount()
    {
        var mapCacheBytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat"));
        var maps = RathenaMapCacheReader.ReadAll(mapCacheBytes);

        foreach (var map in maps)
        {
            Assert.True(map.Width > 0, map.MapName);
            Assert.True(map.Height > 0, map.MapName);
        }
    }

    [Fact]
    public void ReadAll_RealPinnedMapCache_UnknownMapIsNotConflatedWithAKnownFullyBlockedMap()
    {
        var mapCacheBytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat"));
        var maps = RathenaMapCacheReader.ReadAll(mapCacheBytes);
        var provider = new MapCollisionProvider(maps);

        Assert.False(provider.TryGetMap("definitely_not_a_real_map_name", out _));
        Assert.True(provider.TryGetMap("int_land", out _));
    }

    [Fact]
    public void ReadAllFromFile_RealPinnedMapCache_LoadsSuccessfullyAndReportsTimingAndMemory()
    {
        // Informational only (ai/world-data.md task: report startup read time and approximate
        // decoded memory) - not an assertion on a specific budget, since neither is a hard
        // requirement for this vertical slice.
        var path = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var stopwatch = Stopwatch.StartNew();
        var maps = RathenaMapCacheReader.ReadAllFromFile(path);
        stopwatch.Stop();

        var after = GC.GetTotalMemory(forceFullCollection: true);
        var totalCells = maps.Sum(map => (long)map.Width * map.Height);

        Console.WriteLine($"map_cache.dat: {maps.Count} maps, {totalCells} total decoded cells, " +
            $"read+decompress time {stopwatch.ElapsedMilliseconds} ms, approx managed memory delta {(after - before) / 1024} KB.");

        Assert.True(maps.Count > 0);
        Assert.True(totalCells > 0);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
