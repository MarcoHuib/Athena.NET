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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
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
