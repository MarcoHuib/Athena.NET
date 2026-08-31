using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class GeneratedWorldRegistryTests
{
    [Fact]
    public void Maps_AreCompleteUniqueAndLookupIsNormalized()
    {
        Assert.Equal(1296, GeneratedMapRegistry.Count);
        Assert.Equal(Enumerable.Range(0, 1296), GeneratedMapRegistry.All.Select(map => map.AssetId).Order());
        Assert.Equal(1296, GeneratedMapRegistry.All.Select(map => map.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(GeneratedMapRegistry.TryGet("IZ_INT03.GAT", out var map));
        Assert.Equal("iz_int03", map.Name);
        Assert.False(GeneratedMapRegistry.TryGet("not_a_real_map.gat", out _));
        Assert.Equal("legacy/rathena/db/map_cache.dat", GeneratedMapRegistry.Get("int_land").Source.File);
        Assert.Equal("legacy/rathena/db/re/map_cache.dat", GeneratedMapRegistry.Get("prontera").Source.File);
    }

    [Fact]
    public void GeneratedCollision_PreservesDimensionsAndCells()
    {
        var definition = GeneratedMapRegistry.Get("prontera");
        using var provider = GeneratedMapCollisionProvider.OpenProduction();
        Assert.True(provider.TryGetMap("prontera", out var collision));
        Assert.True(provider.TryGetMap("PRONTERA.GAT", out var cached));
        Assert.Same(collision, cached);
        Assert.Equal(definition.Width, collision.Width);
        Assert.Equal(definition.Height, collision.Height);
        Assert.Equal("prontera", collision.MapName);
    }

    [Fact]
    public void Warps_AreCompleteUniqueAndEveryMapResolves()
    {
        Assert.Equal(4468, GeneratedWarpRegistry.Count);
        Assert.Equal(4468, GeneratedWarpRegistry.All.Select(warp => (warp.Source.File, warp.Source.Line)).Distinct().Count());
        Assert.All(GeneratedWarpRegistry.All, warp =>
        {
            Assert.True(GeneratedMapRegistry.TryGet(warp.SourceMap, out _), warp.SourceMap);
            Assert.True(GeneratedMapRegistry.TryGet(warp.DestinationMap, out _), warp.DestinationMap);
            Assert.Equal("rAthena", warp.Source.Repository);
            Assert.Equal("e985006171d2eb320ee512a653f4c83aea3d81b6", warp.Source.Commit);
            Assert.True(warp.Source.Line > 0);
        });
        Assert.Empty(GeneratedWarpRegistry.GetForMap("not_a_real_map"));
        Assert.False(GeneratedWarpRegistry.TryGetForMap("not_a_real_map", out var missing));
        Assert.Empty(missing);
    }

    [Fact]
    public void Runtime_ActivatesOnlyWarpsWhoseSourceMapIsServed()
    {
        var served = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "prontera" };
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), servedMaps: served);
        Assert.Equal(GeneratedWarpRegistry.GetForMap("prontera").Count, world.Maps.StaticWarpCount);
        Assert.DoesNotContain(GeneratedWarpRegistry.All.Where(warp => !served.Contains(warp.SourceMap)), warp => world.Maps.TryFindWarp(warp.SourceMap, warp.SourceX, warp.SourceY, out _));
    }

    [Theory]
    [InlineData("iz_int03", "iz_int03.gat")]
    [InlineData("IZ_INT03.GAT", "IZ_INT03.GAT")]
    public void MapName_NormalizesWorldAndClientForms(string input, string expectedClient)
    {
        Assert.Equal("iz_int03", MapName.NormalizeWorld(input).ToLowerInvariant());
        Assert.Equal(expectedClient, MapName.ToClient(input));
    }
}
