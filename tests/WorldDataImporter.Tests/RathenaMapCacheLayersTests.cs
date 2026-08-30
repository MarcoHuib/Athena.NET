using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Athena.Rathena.Data;

// Priority 1/18 (ai/world-data.md): regression coverage for the shared, pure map-cache layering
// resolver RepositoryDomainAnalyzers.AnalyzeMaps now delegates to (RathenaMapCacheLayers.Merge),
// proving the fix for the "8 maps only" bug where db/re/map_cache.dat (a small Renewal-specific
// OVERLAY) was previously treated as a complete REPLACEMENT of the ~3MB base db/map_cache.dat.
// Mirrors the synthetic map_cache.dat builder pattern already used by
// tests/MapServer.Tests/World/RathenaMapCacheReaderTests.cs and MapCollisionStartupLoaderTests.cs
// (see those files' own doc comments for the exact pinned byte trace) so this project's two
// consumers of the shared resolver (MapCollisionStartupLoader in MapServer, AnalyzeMaps here) are
// proven against the identical fixture shape.
public sealed class RathenaMapCacheLayersTests
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
        Encoding.ASCII.GetBytes(name).CopyTo(record, 0);
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
        foreach (var record in records) { record.CopyTo(buffer, offset); offset += record.Length; }
        return buffer;
    }

    // RathenaMapCacheFormat.ReadAll requires the decompressed payload to be EXACTLY width*height
    // bytes (one raw GAT cell-type byte per cell, map.cpp:3710-3711) - a 1x1 map therefore needs
    // exactly 1 raw cell byte, a 2x2 map exactly 4, etc. FourCells is only valid paired with
    // (short)2, (short)2 below; OneCell is only valid paired with (short)1, (short)1.
    private static readonly byte[] OneCell = [0];
    private static readonly byte[] FourCells = [0, 0, 0, 0];
    private static readonly byte[] NineCells = [0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void Merge_BaseOnly_ResolvesEveryBaseMapAsBaseProvenance()
    {
        var baseCache = BuildMapCache(("prontera", (short)1, (short)1, OneCell), ("izlude", (short)1, (short)1, OneCell));

        var resolved = RathenaMapCacheLayers.Merge(baseCache);

        Assert.Equal(2, resolved.Count);
        Assert.All(resolved, item => Assert.Equal(RathenaMapCacheLayers.Provenance.Base, item.Source));
        Assert.Contains(resolved, item => item.Entry.Name == "prontera");
        Assert.Contains(resolved, item => item.Entry.Name == "izlude");
    }

    [Fact]
    public void Merge_RenewalOverlay_AddsNewMapsAndOverridesExistingOnesFromBase()
    {
        var baseCache = BuildMapCache(("prontera", (short)1, (short)1, OneCell), ("izlude", (short)1, (short)1, OneCell));
        var renewal = BuildMapCache(("prontera", (short)2, (short)2, FourCells), ("prt_fild08", (short)1, (short)1, OneCell));

        var resolved = RathenaMapCacheLayers.Merge(baseCache, renewalOverlay: renewal);

        Assert.Equal(3, resolved.Count);
        var prontera = Assert.Single(resolved, item => item.Entry.Name == "prontera");
        Assert.Equal(RathenaMapCacheLayers.Provenance.RenewalOverlay, prontera.Source);
        Assert.Equal(2, prontera.Entry.Width);
        var izlude = Assert.Single(resolved, item => item.Entry.Name == "izlude");
        Assert.Equal(RathenaMapCacheLayers.Provenance.Base, izlude.Source);
        var newField = Assert.Single(resolved, item => item.Entry.Name == "prt_fild08");
        Assert.Equal(RathenaMapCacheLayers.Provenance.RenewalOverlay, newField.Source);
    }

    [Fact]
    public void Merge_ImportOverlay_WinsOverBothRenewalAndBase()
    {
        var baseCache = BuildMapCache(("prontera", (short)1, (short)1, OneCell));
        var renewal = BuildMapCache(("prontera", (short)2, (short)2, FourCells));
        var import = BuildMapCache(("prontera", (short)3, (short)3, NineCells));

        var resolved = RathenaMapCacheLayers.Merge(baseCache, renewalOverlay: renewal, importOverlay: import);

        var prontera = Assert.Single(resolved);
        Assert.Equal(RathenaMapCacheLayers.Provenance.ImportOverlay, prontera.Source);
        Assert.Equal(3, prontera.Entry.Width);
    }

    [Fact]
    public void Merge_MobSpawnOnlyBaseMap_StillResolvesEvenWhenARenewalOverlayCacheAlsoExists()
    {
        // Regression guard for the fixed 8-map bug: a base-only map (e.g. a field map never
        // overridden by db/re/map_cache.dat) must remain resolvable once a renewal overlay is
        // ALSO present covering wholly different maps - the merge must never behave as if the
        // overlay's presence alone hides base-only entries.
        var baseCache = BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell), ("moc_fild07", (short)1, (short)1, OneCell));
        var renewal = BuildMapCache(("prontera", (short)1, (short)1, OneCell));

        var resolved = RathenaMapCacheLayers.Merge(baseCache, renewalOverlay: renewal);
        var names = resolved.Select(item => item.Entry.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(3, resolved.Count);
        Assert.Contains("prt_fild08", names);
        Assert.Contains("moc_fild07", names);
        Assert.Contains("prontera", names);
        Assert.Equal(RathenaMapCacheLayers.Provenance.Base, resolved.Single(item => item.Entry.Name == "prt_fild08").Source);
    }

    [Fact]
    public void Merge_AbsentOptionalLayers_IsSilentlyFine()
    {
        var baseCache = BuildMapCache(("prontera", (short)1, (short)1, OneCell));

        var resolved = RathenaMapCacheLayers.Merge(baseCache, renewalOverlay: null, importOverlay: null);

        Assert.Single(resolved);
    }

    [Fact]
    public void Merge_DuplicateMapNameWithinTheSameLayerFile_ThrowsWithThatLayerIdentified()
    {
        var baseCache = BuildMapCache(("prontera", (short)1, (short)1, OneCell), ("prontera", (short)2, (short)2, OneCell));

        var exception = Assert.Throws<MapCacheLayerException>(() => RathenaMapCacheLayers.Merge(baseCache));
        Assert.Equal("db/map_cache.dat", exception.Layer);
    }

    [Fact]
    public void Merge_DuplicateMapNameWithinTheRenewalOverlayItself_ThrowsWithThatLayerIdentified()
    {
        var baseCache = BuildMapCache(("prontera", (short)1, (short)1, OneCell));
        var renewal = BuildMapCache(("izlude", (short)1, (short)1, OneCell), ("izlude", (short)2, (short)2, OneCell));

        var exception = Assert.Throws<MapCacheLayerException>(() => RathenaMapCacheLayers.Merge(baseCache, renewalOverlay: renewal));
        Assert.Equal("db/re/map_cache.dat", exception.Layer);
    }

    [Fact]
    public void Merge_MalformedPresentOptionalLayer_ThrowsClearlyRatherThanSilentlyIgnoringIt()
    {
        var baseCache = BuildMapCache(("prontera", (short)1, (short)1, OneCell));
        byte[] malformedRenewal = [1, 2, 3];

        var exception = Assert.Throws<MapCacheLayerException>(() => RathenaMapCacheLayers.Merge(baseCache, renewalOverlay: malformedRenewal));
        Assert.Equal("db/re/map_cache.dat", exception.Layer);
    }

    [Fact]
    public void Merge_MalformedBaseLayer_ThrowsClearly()
    {
        byte[] malformedBase = [9, 9];

        var exception = Assert.Throws<MapCacheLayerException>(() => RathenaMapCacheLayers.Merge(malformedBase));
        Assert.Equal("db/map_cache.dat", exception.Layer);
    }
}
