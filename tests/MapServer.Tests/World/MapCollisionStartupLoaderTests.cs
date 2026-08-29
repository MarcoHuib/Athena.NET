using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class MapCollisionStartupLoaderTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "athena-map-collision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // Mirrors MapCollisionArtifact's own layout (see that type's doc comment) - a tiny synthetic
    // artifact, never real Gravity map bytes.
    private static byte[] BuildArtifact(string mapName, int width, int height, byte[] cellBytes)
    {
        var nameBytes = Encoding.UTF8.GetBytes(mapName);
        var buffer = new byte[4 + 4 + nameBytes.Length + 4 + 4 + cellBytes.Length];
        var offset = 0;

        "AMC1"u8.ToArray().CopyTo(buffer, offset); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)nameBytes.Length); offset += 4;
        nameBytes.CopyTo(buffer, offset); offset += nameBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), width); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), height); offset += 4;
        cellBytes.CopyTo(buffer, offset);

        return buffer;
    }

    [Fact]
    public void Load_NoArtifacts_ReturnsEmptyProvider()
    {
        var provider = MapCollisionStartupLoader.Load([]);

        Assert.Same(EmptyMapCollisionProvider.Instance, provider);
    }

    [Fact]
    public void Load_OneArtifact_ResolvesConfiguredLogicalMap()
    {
        var tempDir = CreateTempDir();
        var artifactPath = Path.Combine(tempDir, "int_land.athmap");
        File.WriteAllBytes(artifactPath, BuildArtifact("int_land", 1, 1, [(byte)MapCellFlags.Walkable]));

        var provider = MapCollisionStartupLoader.Load([new MapCollisionArtifactConfig(artifactPath, ["int_land"])]);

        Assert.True(provider.TryGetMap("int_land", out var map));
        Assert.True(map.IsWalkable(0, 0));
    }

    [Fact]
    public void Load_MultipleLogicalAliases_ResolveToTheSameImmutableMapInstance()
    {
        var tempDir = CreateTempDir();
        var artifactPath = Path.Combine(tempDir, "int_land.athmap");
        File.WriteAllBytes(artifactPath, BuildArtifact("int_land", 1, 1, [(byte)MapCellFlags.Walkable]));

        var provider = MapCollisionStartupLoader.Load(
        [
            new MapCollisionArtifactConfig(artifactPath, ["int_land", "int_land01", "int_land02", "int_land03", "int_land04"]),
        ]);

        Assert.True(provider.TryGetMap("int_land", out var baseMap));
        Assert.True(provider.TryGetMap("int_land01", out var alias01));
        Assert.True(provider.TryGetMap("int_land04", out var alias04));

        // Same underlying artifact load, not five independent copies - proven by reference
        // identity, not merely equal field values.
        Assert.Same(baseMap, alias01);
        Assert.Same(baseMap, alias04);
    }

    [Fact]
    public void Load_UnconfiguredLogicalMap_ReturnsNoMap()
    {
        var tempDir = CreateTempDir();
        var artifactPath = Path.Combine(tempDir, "int_land.athmap");
        File.WriteAllBytes(artifactPath, BuildArtifact("int_land", 1, 1, [(byte)MapCellFlags.Walkable]));

        var provider = MapCollisionStartupLoader.Load([new MapCollisionArtifactConfig(artifactPath, ["int_land"])]);

        Assert.False(provider.TryGetMap("some_other_map", out _));
    }

    [Fact]
    public void Load_DuplicateLogicalAliasAcrossArtifacts_ThrowsClearly()
    {
        var tempDir = CreateTempDir();
        var pathA = Path.Combine(tempDir, "a.athmap");
        var pathB = Path.Combine(tempDir, "b.athmap");
        File.WriteAllBytes(pathA, BuildArtifact("a", 1, 1, [(byte)MapCellFlags.Walkable]));
        File.WriteAllBytes(pathB, BuildArtifact("b", 1, 1, [(byte)MapCellFlags.Walkable]));

        var ex = Assert.Throws<InvalidOperationException>(() => MapCollisionStartupLoader.Load(
        [
            new MapCollisionArtifactConfig(pathA, ["int_land"]),
            new MapCollisionArtifactConfig(pathB, ["int_land"]),
        ]));
        Assert.Contains("int_land", ex.Message);
    }

    [Fact]
    public void Load_DuplicateLogicalAliasWithinOneArtifactEntry_ThrowsClearly()
    {
        var tempDir = CreateTempDir();
        var artifactPath = Path.Combine(tempDir, "int_land.athmap");
        File.WriteAllBytes(artifactPath, BuildArtifact("int_land", 1, 1, [(byte)MapCellFlags.Walkable]));

        Assert.Throws<InvalidOperationException>(() => MapCollisionStartupLoader.Load(
        [
            new MapCollisionArtifactConfig(artifactPath, ["int_land", "int_land"]),
        ]));
    }

    [Fact]
    public void Load_MissingConfiguredArtifactFile_ThrowsClearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MapCollisionStartupLoader.Load(
        [
            new MapCollisionArtifactConfig("/definitely/not/a/real/path.athmap", ["int_land"]),
        ]));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_MalformedArtifactFile_ThrowsClearly()
    {
        var tempDir = CreateTempDir();
        var artifactPath = Path.Combine(tempDir, "corrupt.athmap");
        File.WriteAllBytes(artifactPath, [0x00, 0x01, 0x02]); // Too short to even have a header.

        Assert.Throws<InvalidOperationException>(() => MapCollisionStartupLoader.Load(
        [
            new MapCollisionArtifactConfig(artifactPath, ["int_land"]),
        ]));
    }

    [Fact]
    public void Load_MapCachePathConfigured_LoadsRealPinnedMapCache()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");

        var provider = MapCollisionStartupLoader.Load([], mapCachePath);

        Assert.True(provider.TryGetMap("int_land", out var map));
        Assert.Equal(140, map.Width);
        Assert.Equal(140, map.Height);
    }

    [Fact]
    public void Load_MissingMapCachePath_ThrowsClearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MapCollisionStartupLoader.Load([], "/definitely/not/a/real/map_cache.dat"));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Regression coverage for the Aspire incident: a relative map_cache_path silently resolved
    // against the wrong process CWD, and the original exception only echoed back the unresolved
    // configured string, giving no clue what path was actually attempted. The exception must now
    // include the RESOLVED absolute path so a CWD mismatch is immediately diagnosable from the
    // error alone.
    [Fact]
    public void Load_MissingRelativeMapCachePath_ExceptionIncludesResolvedAbsolutePath()
    {
        var relativePath = "definitely/not/a/real/map_cache.dat";
        var expectedResolved = Path.GetFullPath(relativePath);

        var ex = Assert.Throws<InvalidOperationException>(() => MapCollisionStartupLoader.Load([], relativePath));

        Assert.Contains(relativePath, ex.Message);
        Assert.Contains(expectedResolved, ex.Message);
    }

    [Fact]
    public void Load_MalformedMapCachePath_ThrowsClearly()
    {
        var tempDir = CreateTempDir();
        var mapCachePath = Path.Combine(tempDir, "corrupt.dat");
        File.WriteAllBytes(mapCachePath, [0x00, 0x01]); // Too short to even have a header.

        Assert.Throws<InvalidOperationException>(() => MapCollisionStartupLoader.Load([], mapCachePath));
    }

    [Fact]
    public void Load_MapCachePathTakesNoArtifactsGiven_ReturnsUsableProvider()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");

        var provider = MapCollisionStartupLoader.Load([], mapCachePath);

        Assert.False(provider.TryGetMap("definitely_not_a_real_map", out _));
    }

    // Live acceptance regression (Prontera crash root cause): pinned "prontera" geometry exists
    // ONLY in db/re/map_cache.dat (312x392), never in the generic db/map_cache.dat this project
    // was previously loading alone - confirmed independently via RathenaMapCacheReaderTests'
    // ReadAllFromFile_RealPinnedMapCache_ProperProntheraRecordIsGenuinelyAbsent_OnlyPprronteraExists
    // for the generic file. This proves the REAL production call shape (Renewal ruleset, the
    // actual configured map_cache_path) now resolves "prontera" by merging in the ruleset-specific
    // overlay, matching pinned rAthena's own map_readallmaps load order (map.cpp:3908-3943).
    [Fact]
    public void Load_RenewalRuleSet_RealPinnedMapCache_ResolvesProntheraViaRulesetOverlay()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");

        var provider = MapCollisionStartupLoader.Load([], mapCachePath, Athena.Net.MapServer.Gameplay.Rules.RagnarokRuleSet.Renewal);

        Assert.True(provider.TryGetMap("prontera", out var map));
        Assert.Equal(312, map.Width);
        Assert.Equal(392, map.Height);
    }

    // The overlay must never shadow maps the generic file ALREADY correctly provides - db/re/
    // map_cache.dat is a small, curated 8-map set (alberta, izlude, morocc, prontera, prt_church,
    // prt_fild05, prt_fild08, prt_in per an independent parse), so every other real travel-corridor
    // map (izlude_d, prt_fild08d, int_land04, etc., none of which db/re/map_cache.dat declares at
    // all) must still resolve from the generic fallback exactly as before this fix.
    [Fact]
    public void Load_RenewalRuleSet_RealPinnedMapCache_StillResolvesGenericFallbackMapsNotInTheOverlay()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");

        var provider = MapCollisionStartupLoader.Load([], mapCachePath, Athena.Net.MapServer.Gameplay.Rules.RagnarokRuleSet.Renewal);

        Assert.True(provider.TryGetMap("int_land", out _));
        Assert.True(provider.TryGetMap("int_land04", out _));
        Assert.True(provider.TryGetMap("izlude_d", out _));
        Assert.True(provider.TryGetMap("prt_fild08d", out _));
        Assert.True(provider.TryGetMap("iz_int04", out _));
    }

    // Without the ruleset-specific overlay (e.g. PreRenewal, which resolves to
    // db/pre-re/map_cache.dat - a DIFFERENT file, not tested here, but the DEFAULT-parameter
    // omission case below exercises the "no explicit ruleset passed" call shape existing callers
    // still use) the loader must not regress to failing outright - it still returns a usable
    // provider from the generic file alone.
    [Fact]
    public void Load_NoRuleSetArgumentGiven_DefaultsToRenewal_StillResolvesProntera()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");

        var provider = MapCollisionStartupLoader.Load([], mapCachePath); // ruleSet omitted - defaults to Renewal.

        Assert.True(provider.TryGetMap("prontera", out _));
    }

    // Full end-to-end acceptance proof (task requirement 4: "MapServer startup must prove every
    // declared served map has collision"): the EXACT production startup sequence
    // (MapServerApp.RunAsync's own configured map_cache_path + Renewal ruleset, then
    // MapServerHostingScope.RequireCollisionForAllServedMaps) must now succeed with the real
    // pinned map_cache.dat - it previously would have thrown for "prontera" before this fix.
    [Fact]
    public void Load_RealPinnedMapCache_ProductionStartupSequence_AllServedMapsResolve()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");

        var provider = MapCollisionStartupLoader.Load([], mapCachePath, Athena.Net.MapServer.Gameplay.Rules.RagnarokRuleSet.Renewal);

        MapServerHostingScope.RequireCollisionForAllServedMaps(provider); // No exception - every ServedMaps entry resolves.
    }

    // Incidental finding from this same fix: db/re/map_cache.dat's overlay ALSO happens to include
    // the generic/base "prt_fild08" (400x400) - the exact map MapServerHostingScope's own doc
    // comment previously documented as lacking collision data and therefore deliberately excluded
    // from ServedMaps. This does NOT change ServedMaps itself (a scope decision, not made here) -
    // only proves the underlying data now genuinely resolves, for whoever revisits that exclusion.
    [Fact]
    public void Load_RenewalRuleSet_RealPinnedMapCache_PrtFild08BaseMapNowResolvesViaOverlay()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");

        var provider = MapCollisionStartupLoader.Load([], mapCachePath, Athena.Net.MapServer.Gameplay.Rules.RagnarokRuleSet.Renewal);

        Assert.True(provider.TryGetMap("prt_fild08", out var map));
        Assert.Equal(400, map.Width);
        Assert.Equal(400, map.Height);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

    // The precedence rule itself (`options.MapCachePathOverride ?? mergedConfig.MapCachePath`,
    // MapServerApp.RunAsync) is a one-line ??; these tests exercise the effect that rule has on
    // MapCollisionStartupLoader.Load - the "effective path" it receives is uniform regardless of
    // whether it came from an explicit --map-cache-path override or the configured map_cache_path
    // value, so proving the loader handles an ABSOLUTE Aspire-style path exactly like a relative
    // one is what actually matters here (StartupOptionsTests covers the CLI-parsing half of the
    // precedence rule: that --map-cache-path is captured into MapCachePathOverride at all).

    [Fact]
    public void Load_AbsoluteAspireStylePath_LoadsRealPinnedMapCache_JustLikeARelativePath()
    {
        // Simulates the exact effective path Aspire's AppHost supplies via --map-cache-path: an
        // absolute path built from its own discovered repository root
        // (Path.Combine(repoRoot, "legacy", "rathena", "db", "map_cache.dat")), not the
        // CWD-relative "legacy/rathena/db/map_cache.dat" direct-local-execution/Docker use.
        var absoluteMapCachePath = Path.Combine(FindRepositoryRoot(), "legacy", "rathena", "db", "map_cache.dat");
        Assert.True(Path.IsPathRooted(absoluteMapCachePath));

        var provider = MapCollisionStartupLoader.Load([], absoluteMapCachePath);

        Assert.True(provider.TryGetMap("int_land", out var map));
        Assert.Equal(140, map.Width);
        Assert.Equal(140, map.Height);
    }

    [Fact]
    public void Load_ExplicitOverridePathThatDoesNotExist_FailsLoudly_NeverSilentlyIgnoredInFavorOfSomeOtherSource()
    {
        // An operator/launcher that explicitly supplies an override must be told loudly if THAT
        // specific path is wrong - Load has no "other" config path to silently fall back to here
        // (there is only ever one effective path by the time it reaches Load), but this proves the
        // failure is exactly as loud for an absolute, override-shaped path as for a relative one.
        var bogusAbsolutePath = Path.Combine(FindRepositoryRoot(), "legacy", "rathena", "db", "does_not_exist.dat");

        var ex = Assert.Throws<InvalidOperationException>(() => MapCollisionStartupLoader.Load([], bogusAbsolutePath));

        Assert.Contains(bogusAbsolutePath, ex.Message);
    }

    [Fact]
    public void Load_ResultingProviderIsUsableAndImmutable()
    {
        var tempDir = CreateTempDir();
        var artifactPath = Path.Combine(tempDir, "int_land.athmap");
        File.WriteAllBytes(artifactPath, BuildArtifact("int_land", 2, 1, [(byte)MapCellFlags.Walkable, (byte)MapCellFlags.None]));

        var provider = MapCollisionStartupLoader.Load([new MapCollisionArtifactConfig(artifactPath, ["int_land"])]);

        // Two independent lookups must observe the exact same object/state - nothing about the
        // provider or its maps mutates between reads.
        Assert.True(provider.TryGetMap("int_land", out var first));
        Assert.True(provider.TryGetMap("int_land", out var second));
        Assert.Same(first, second);
        Assert.Equal(2, first.Width);
        Assert.True(first.IsWalkable(0, 0));
        Assert.False(first.IsWalkable(1, 0));
    }
}
