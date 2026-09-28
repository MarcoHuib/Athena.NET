using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.World;
using Athena.Net.Shared.MapIdentity;

namespace Athena.Net.MapServer.Tests.World;

// Channel removal at the MapServer/World boundary: the 24 legacy copy maps are not hosted, own no
// monster simulation and no warp/actor surface, while their pinned/generated source stays available as
// source coverage (source representation is not runtime activation).
public sealed class CanonicalMapHostingTests
{
    private static readonly string[] Aliases =
    [
        "izlude_a", "izlude_b", "izlude_c", "izlude_d",
        "prt_fild08a", "prt_fild08b", "prt_fild08c", "prt_fild08d",
        "iz_int01", "iz_int02", "iz_int03", "iz_int04",
        "int_land01", "int_land02", "int_land03", "int_land04",
        "iz_ac01_a", "iz_ac01_b", "iz_ac01_c", "iz_ac01_d",
        "iz_ac02_a", "iz_ac02_b", "iz_ac02_c", "iz_ac02_d",
    ];

    [Fact]
    public void AliasTable_IsTheExplicitTwentyFour_ToSixCanonicalMaps()
    {
        Assert.Equal(24, CanonicalMapPolicy.AliasTable.Count);
        Assert.Equal(Aliases.Order(StringComparer.Ordinal), CanonicalMapPolicy.AliasTable.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "int_land", "iz_ac01", "iz_ac02", "iz_int", "izlude", "prt_fild08" }, CanonicalMapPolicy.CanonicalMaps.Order(StringComparer.Ordinal));
        foreach (var alias in Aliases)
        {
            Assert.True(CanonicalMapPolicy.IsAlias(alias));
            Assert.Contains(CanonicalMapPolicy.Canonicalize(alias), CanonicalMapPolicy.CanonicalMaps);
        }
        foreach (var canonical in CanonicalMapPolicy.CanonicalMaps)
        {
            Assert.False(CanonicalMapPolicy.IsAlias(canonical));
            Assert.Equal(canonical, CanonicalMapPolicy.Canonicalize(canonical));
        }
    }

    [Theory]
    [InlineData("izlude_d", "izlude")]
    [InlineData("IZLUDE_D", "izlude")]
    [InlineData("prt_fild08c.gat", "prt_fild08")]
    [InlineData("iz_ac01_b", "iz_ac01")]
    [InlineData("int_land04", "int_land")]
    public void Canonicalize_IsCaseInsensitive_AndToleratesTheGatSuffix(string input, string expected) =>
        Assert.Equal(expected, CanonicalMapPolicy.Canonicalize(input));

    [Theory]
    [InlineData("prontera")]
    [InlineData("prt_fild07")]
    [InlineData("izlude_in")]
    [InlineData("new_1-1")]
    [InlineData("new_1-2")]
    [InlineData("new_5-1")]
    [InlineData("1@tower")]
    [InlineData("pvp_n_1-1")]
    [InlineData("iz_int05")]
    [InlineData("izlude_e")]
    [InlineData("prt_fild08e")]
    [InlineData("int_land00")]
    [InlineData("iz_ac03_a")]
    [InlineData("")]
    public void UnrelatedMaps_AreNeverCanonicalized_NoSuffixStripping(string map) => Assert.Equal(map, CanonicalMapPolicy.Canonicalize(map));

    [Fact]
    public void ActiveRuntimeHosting_ContainsNoAliasMap()
    {
        Assert.DoesNotContain(MapServerHostingScope.ServedMaps, map => CanonicalMapPolicy.IsAlias(map));
        Assert.DoesNotContain(MapServerHostingScope.MobSpawnMaps, map => CanonicalMapPolicy.IsAlias(map));
        // The canonical corridor stays hosted.
        Assert.Superset(new HashSet<string>(["int_land", "iz_ac01", "iz_ac02", "iz_int", "izlude", "prt_fild08", "prontera"]), MapServerHostingScope.ServedMaps.ToHashSet());
        Assert.Superset(new HashSet<string>(["int_land", "prt_fild08", "prontera"]), MapServerHostingScope.MobSpawnMaps.ToHashSet());
        // The Academy floors are hosted (warps/collision/actors) but spawn nothing at runtime: their only
        // effective spawns (4 training dummies) are a separate content decision.
        Assert.DoesNotContain("iz_ac01", MapServerHostingScope.MobSpawnMaps);
        Assert.DoesNotContain("iz_ac02", MapServerHostingScope.MobSpawnMaps);
    }

    [Fact]
    public void ProductionWorld_HostsOnlyCanonicalMaps_ForSpawnsWarpsAndActors()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);

        // One monster simulation per canonical map: no spawn (hence no World simulation, no actor-id
        // space) exists for any alias.
        Assert.NotEmpty(world.MonsterSpawns);
        Assert.DoesNotContain(world.MonsterSpawns, spawn => CanonicalMapPolicy.IsAlias(spawn.Map));
        Assert.All(world.MonsterSpawns, spawn => Assert.Contains(spawn.Map, MapServerHostingScope.MobSpawnMaps, StringComparer.OrdinalIgnoreCase));

        // Alias warp surfaces are inert.
        foreach (var alias in Aliases)
            Assert.Empty(GeneratedWarpLoadProfiles.GetForMap(alias, WarpLoadProfile.AthenaIroEffective).Where(warp => world.Maps.TryFindWarp(warp.SourceMap, warp.SourceX, warp.SourceY, out _)));
    }

    [Fact]
    public void PinnedSource_ForTheAliases_IsStillRepresented_AsSourceCoverage()
    {
        // Not runtime-active, but the generated source knowledge remains available for reference.
        foreach (var alias in new[] { "prt_fild08c", "izlude_d", "int_land04" })
        {
            Assert.True(GeneratedMapRegistry.TryGet(alias, out _), alias);
            Assert.True(GeneratedMobSpawnRegistry.GetForMap(alias).Count > 0 || GeneratedWarpRegistry.GetForMap(alias).Count > 0, alias);
        }
        Assert.NotEmpty(GeneratedMobSpawnRegistry.GetForMap("prt_fild08c"));
        Assert.NotEmpty(GeneratedWarpRegistry.GetForMap("izlude_d"));
    }

    // ---- Canonical Academy route (Izlude <-> iz_ac01 <-> iz_ac02) ----------------------------------------

    private static WarpDefinition Warp(string map, ushort x, ushort y)
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp(map, x, y, out var warp), $"No active warp at {map} ({x},{y})");
        Assert.Equal("legacy/rathena/npc/re/warps/cities/izlude.txt", warp.Source.File); // The Renewal row, never a pre-re one.
        return warp;
    }

    [Fact]
    public void AcademyRoute_IzludeEntersIzAc01_AndIzAc01LeavesBackToIzlude()
    {
        var enter = Warp("izlude", 125, 257);
        Assert.Equal(("iz_ac01", (ushort)99, (ushort)29), (enter.DestinationMap, enter.DestinationX, enter.DestinationY));
        var leave = Warp("iz_ac01", 100, 24);
        Assert.Equal(("izlude", (ushort)127, (ushort)253), (leave.DestinationMap, leave.DestinationX, leave.DestinationY));
    }

    [Fact]
    public void AcademyRoute_IzAc01ReachesIzAc02_AndIzAc02ReturnsToIzAc01()
    {
        var up = Warp("iz_ac01", 78, 25);
        Assert.Equal(("iz_ac02", (ushort)207, (ushort)27), (up.DestinationMap, up.DestinationX, up.DestinationY));
        var down = Warp("iz_ac02", 198, 27);
        Assert.Equal(("iz_ac01", (ushort)78, (ushort)28), (down.DestinationMap, down.DestinationX, down.DestinationY));
    }

    [Fact]
    public void NoAcademyAliasMapIsHosted_AndNoWarpOnAHostedMapLeadsIntoAnUnhostedCanonicalOrAliasMap()
    {
        foreach (var alias in Aliases.Where(alias => alias.StartsWith("iz_ac", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(alias, MapServerHostingScope.ServedMaps);
            Assert.Empty(GeneratedWarpLoadProfiles.GetForMap(alias, WarpLoadProfile.AthenaIroEffective).Where(warp => MapServerHostingScope.ServedMaps.Contains(warp.SourceMap)));
        }

        // Any Renewal-effective warp (from a hosted map) whose destination is a channel copy or a canonical
        // channel-family map must land on a HOSTED canonical map once the destination is canonicalized.
        foreach (var map in MapServerHostingScope.ServedMaps)
            foreach (var warp in GeneratedWarpLoadProfiles.GetForMap(map, WarpLoadProfile.AthenaIroEffective))
            {
                var destination = CanonicalMapPolicy.Canonicalize(warp.DestinationMap);
                if (CanonicalMapPolicy.CanonicalMaps.Contains(destination))
                    Assert.True(MapServerHostingScope.ServedMaps.Contains(destination), $"{warp.Name}: {warp.SourceMap} -> {warp.DestinationMap} leads into unhosted '{destination}'");
            }

        // Explicit: the canonical Academy floors are hosted and have their own active warp surface.
        foreach (var canonical in new[] { "iz_ac01", "iz_ac02" })
        {
            Assert.Contains(canonical, MapServerHostingScope.ServedMaps);
            Assert.NotEmpty(GeneratedWarpLoadProfiles.GetForMap(canonical, WarpLoadProfile.AthenaIroEffective));
        }
    }

    [Fact]
    public void ServedMapCollision_CoversTheAcademyFloors()
    {
        using var provider = GeneratedMapCollisionProvider.Open(Athena.Net.MapServer.Tests.Testing.TestGeneratedMapAssets.MapPackPath);
        MapServerHostingScope.RequireCollisionForAllServedMaps(provider); // Startup validation covers iz_ac01/iz_ac02 via ServedMaps.
        Assert.True(provider.TryGetMap("iz_ac01", out _));
        Assert.True(provider.TryGetMap("iz_ac02", out _));
    }

    // ---- Generic resolver (no map knowledge) ----------------------------------------------------------

    [Fact]
    public void Resolver_RejectsChainsDuplicatesAndSelfAliases_AndKnowsOnlyTheTableItIsGiven()
    {
        Assert.Throws<InvalidOperationException>(() => CanonicalMapPolicy.BuildAliasTable([("a", "b"), ("b", "c")]));
        Assert.Throws<InvalidOperationException>(() => CanonicalMapPolicy.BuildAliasTable([("a", "b"), ("a", "c")]));
        Assert.Throws<InvalidOperationException>(() => CanonicalMapPolicy.BuildAliasTable([("a", "A")]));
        var table = CanonicalMapPolicy.BuildAliasTable([("x_a", "x")]);
        Assert.Equal("x", table["x_a"]);
        Assert.False(table.ContainsKey("x_b")); // A same-shaped name that is not listed is not an alias.
    }

    // The premise of canonicalizing a persisted position onto the canonical map at the SAME coordinates:
    // every alias's collision data is cell-for-cell identical to its canonical map's.
    [Fact]
    public void AliasMaps_HaveCellForCellIdenticalGeometryToTheirCanonicalMap()
    {
        using var provider = GeneratedMapCollisionProvider.Open(Athena.Net.MapServer.Tests.Testing.TestGeneratedMapAssets.MapPackPath);
        foreach (var (alias, canonical) in CanonicalMapPolicy.AliasTable)
        {
            Assert.True(provider.TryGetMap(alias, out var aliasMap), alias);
            Assert.True(provider.TryGetMap(canonical, out var canonicalMap), canonical);
            Assert.Equal((canonicalMap.Width, canonicalMap.Height), (aliasMap.Width, aliasMap.Height));
            for (var y = 0; y < canonicalMap.Height; y++)
                for (var x = 0; x < canonicalMap.Width; x++)
                    Assert.True(canonicalMap.GetCell(x, y) == aliasMap.GetCell(x, y), $"{alias} differs from {canonical} at ({x},{y})");
        }
    }
}
