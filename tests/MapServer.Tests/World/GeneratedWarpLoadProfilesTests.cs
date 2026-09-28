using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Warp source-layer semantics (ai/world-data.md "Generated warps"). Pinned rAthena carries BOTH
// npc/pre-re/warps/** and npc/re/warps/** rows for the same trigger cells; GeneratedWarpRegistry keeps
// every row as source coverage, but the runtime must expose only the Renewal-effective set
// (GeneratedWarpLoadProfiles.AthenaIroEffective), independent of file/registry order.
public sealed class GeneratedWarpLoadProfilesTests
{
    private const string PreRePrefix = "legacy/rathena/npc/pre-re/";

    private static bool IsPreRe(WarpDefinition warp) => warp.Source.File.StartsWith(PreRePrefix, StringComparison.Ordinal);

    // Two rows are in conflict when one row's trigger area covers the other's trigger cell (WarpDefinition.Matches
    // is what TryFindWarp uses) - the exact case where "first in list wins" would pick a layer by ordering.
    private static bool Overlaps(WarpDefinition a, WarpDefinition b) => a.Matches(b.SourceMap, b.SourceX, b.SourceY) || b.Matches(a.SourceMap, a.SourceX, a.SourceY);

    private static IEnumerable<(WarpDefinition PreRe, WarpDefinition Renewal)> Conflicts()
    {
        var effective = GeneratedWarpLoadProfiles.AthenaIroEffective.ToHashSet();
        foreach (var group in GeneratedWarpRegistry.All.GroupBy(warp => warp.SourceMap, StringComparer.OrdinalIgnoreCase))
        {
            var preRe = group.Where(warp => IsPreRe(warp) && !effective.Contains(warp)).ToArray();
            var renewal = group.Where(effective.Contains).ToArray();
            foreach (var pre in preRe)
                foreach (var re in renewal)
                    if (Overlaps(pre, re)) yield return (pre, re);
        }
    }

    [Fact]
    public void Registry_PreservesEverySourceRow_AndProfilesArePartitionedBySourceLoadClass()
    {
        Assert.Equal(4468, GeneratedWarpRegistry.Count);
        Assert.Equal(3874, GeneratedWarpLoadProfiles.RathenaRenewalDefault.Length);
        Assert.Equal(3874, GeneratedWarpLoadProfiles.AthenaIroEffective.Length);
        Assert.Equal(568, GeneratedWarpRegistry.All.Count(IsPreRe));
        // Source coverage is preserved: pre-Renewal rows remain represented in the registry.
        Assert.Contains(GeneratedWarpRegistry.All, warp => warp.Source.File == "legacy/rathena/npc/pre-re/warps/fields/prontera_fild.txt");
    }

    [Fact]
    public void EffectiveProfile_ContainsNoPreRenewalRow_AndEveryRenewalRowIsKept()
    {
        var effective = GeneratedWarpLoadProfiles.AthenaIroEffective;
        Assert.DoesNotContain(effective, IsPreRe);
        Assert.All(GeneratedWarpLoadProfiles.RathenaRenewalDefault, warp => Assert.Contains(warp, effective));
        // Views reference the registry's own instances (no duplicate copies).
        var all = GeneratedWarpRegistry.All.ToHashSet();
        Assert.All(effective, warp => Assert.Contains(warp, all));
        Assert.All(effective.Where(warp => warp.Source.File.StartsWith("legacy/rathena/npc/re/warps/", StringComparison.Ordinal)),
            warp => Assert.Contains(warp, GeneratedWarpLoadProfiles.RathenaRenewalDefault));
    }

    [Fact]
    public void EveryRealPreReVsRenewalConflict_ResolvesToTheRenewalRow_EvenWhenPreReSortsFirst()
    {
        var conflicts = Conflicts().ToArray();
        Assert.NotEmpty(conflicts);

        var registryOrder = GeneratedWarpRegistry.All.Select((warp, index) => (warp, index)).ToDictionary(item => item.warp, item => item.index);
        // The bug scenario really exists in the data: at least one pre-re row precedes its Renewal rival.
        Assert.Contains(conflicts, pair => registryOrder[pair.PreRe] < registryOrder[pair.Renewal]);

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()));
        foreach (var (pre, renewal) in conflicts)
        {
            // Probe the pre-re trigger cell itself: whatever is active there must be a Renewal-effective row.
            if (world.Maps.TryFindWarp(pre.SourceMap, pre.SourceX, pre.SourceY, out var found))
                Assert.False(IsPreRe(found), $"{pre.Name} {pre.SourceMap} ({pre.SourceX},{pre.SourceY}) resolved to pre-re row {found.Source.File}:{found.Source.Line}");
        }
    }

    [Fact]
    public void NoRuntimeWarpAnywhereResolvesToAPreRenewalRow()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()));
        foreach (var warp in GeneratedWarpRegistry.All.Where(IsPreRe))
            if (world.Maps.TryFindWarp(warp.SourceMap, warp.SourceX, warp.SourceY, out var found))
                Assert.False(IsPreRe(found), $"{found.Name}: {found.Source.File}:{found.Source.Line}");
    }

    [Fact]
    public void PrtFild08_371_212_ResolvesToIzludeRenewalDestination_NotThePreRenewalOne()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()));

        Assert.True(world.Maps.TryFindWarp("prt_fild08", 371, 212, out var warp));
        Assert.Equal("izlude", warp.DestinationMap);
        Assert.Equal((ushort)24, warp.DestinationX);
        Assert.Equal((ushort)98, warp.DestinationY);
        Assert.Equal("legacy/rathena/npc/re/warps/cities/izlude.txt", warp.Source.File);
        // Both layers are represented as source coverage; only one is active.
        Assert.Contains(GeneratedWarpRegistry.All, w => w.SourceMap == "prt_fild08" && w.SourceX == 371 && w.SourceY == 212 && w.DestinationX == 35 && w.DestinationY == 78 && IsPreRe(w));
        Assert.DoesNotContain(GeneratedWarpLoadProfiles.AthenaIroEffective, w => w.SourceMap == "prt_fild08" && w.DestinationX == 35 && w.DestinationY == 78 && w.DestinationMap == "izlude");
    }

    [Fact]
    public void ProcessDefaultRegistry_UsesTheSameEffectiveProfile()
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("prt_fild08", 371, 212, out var warp));
        Assert.Equal(((ushort)24, (ushort)98), (warp.DestinationX, warp.DestinationY));
    }

    [Fact]
    public void NonConflictingRenewalWarps_RemainActive()
    {
        var hosted = MapServerHostingScope.ServedMaps;
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), servedMaps: hosted);
        var effective = GeneratedWarpLoadProfiles.AthenaIroEffective.ToHashSet();
        var hostedEffective = GeneratedWarpLoadProfiles.AthenaIroEffective.Where(warp => hosted.Contains(warp.SourceMap)).ToArray();

        // Every Renewal-effective warp on a hosted map is active - a Renewal row is never dropped just
        // because a pre-re row exists for the same area.
        Assert.NotEmpty(hostedEffective);
        Assert.Equal(hostedEffective.Length, world.Maps.StaticWarpCount);
        foreach (var warp in hostedEffective)
        {
            Assert.True(world.Maps.TryFindWarp(warp.SourceMap, warp.SourceX, warp.SourceY, out var found), $"{warp.Name} {warp.SourceMap}");
            Assert.Contains(found, effective);
        }
    }

    [Fact]
    public void GetForMap_ReturnsProfileFilteredViews_AndEmptyForUnknownMaps()
    {
        var effective = GeneratedWarpLoadProfiles.GetForMap("prt_fild08", WarpLoadProfile.AthenaIroEffective);
        Assert.NotEmpty(effective);
        Assert.DoesNotContain(effective, IsPreRe);
        Assert.True(GeneratedWarpRegistry.GetForMap("prt_fild08").Count > effective.Count);
        Assert.Empty(GeneratedWarpLoadProfiles.GetForMap("not_a_real_map", WarpLoadProfile.AthenaIroEffective));
        Assert.Empty(GeneratedWarpLoadProfiles.GetForMap("not_a_real_map", WarpLoadProfile.RathenaRenewalDefault));
    }
}
