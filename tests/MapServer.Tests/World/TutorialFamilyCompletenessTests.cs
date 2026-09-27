using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Cross-cutting invariant that both prior tutorial-regression fixes independently satisfied but
// never jointly asserted: for the FULL iz_int{,01,02,03,04}/int_land{,01,02,03,04} start-point
// family, every kind of generated content (NPC placements, scripted warps, navigation, AND mob
// spawns) must cover all five members together - not just each kind individually. This is the
// exact shape of regression that slipped through before: NPC family 5/5, warp family 5/5,
// navigation family 5/5, but mob family only 4/5 (generic int_land's G_PORING spawn was dropped by
// a stale --exclude-map int_land in the compile-mob-spawn regeneration invocation, discovered only
// via manual runtime testing after the NPC/warp/navigation fix already looked complete). A single
// per-kind-count table here would not have caught that: the bug was invisible to any check that
// only asked "is each kind independently non-empty" without cross-referencing family membership.
public sealed class TutorialFamilyCompletenessTests
{
    private static readonly string[] IzIntSuffixes = ["", "01", "02", "03", "04"];

    [Fact]
    public void EveryStartPointFamilyMember_HasNpcWarpNavigationAndMobCoverage()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);
        var registry = WorldMapRegistry.Tutorial;

        var missing = new List<string>();
        foreach (var suffix in IzIntSuffixes)
        {
            var izInt = "iz_int" + suffix;
            var intLand = "int_land" + suffix;
            var introToIzludeSuffix = suffix switch { "" => "", "01" => "_a", "02" => "_b", "03" => "_c", "04" => "_d", _ => throw new ArgumentOutOfRangeException(nameof(suffix)) };

            if (!registry.EntitiesById.ContainsKey($"npc:{izInt}:wounded swordsman#intro_npc01_iz_int{suffix}"))
                missing.Add($"NPC(WoundedSwordsman687) missing for '{izInt}'");
            if (!registry.EntitiesById.ContainsKey($"npc:{izInt}:wounded swordsman#intro_npc02_iz_int{suffix}"))
                missing.Add($"NPC(WoundedSwordsman688) missing for '{izInt}'");
            if (!registry.EntitiesById.ContainsKey($"warp:{izInt}:ship_out{suffix}"))
                missing.Add($"Warp(#ship_out) missing for '{izInt}'");
            if (!registry.EntitiesById.ContainsKey($"npc:{intLand}:captain carocc#intro_npc03{(suffix.Length > 0 ? "_" + suffix : "")}"))
                missing.Add($"NPC(CaptainCarocc) missing for '{intLand}'");
            if (!registry.EntitiesById.ContainsKey($"npc:{intLand}:lumin#new_ship{suffix}"))
                missing.Add($"NPC(Lumin) missing for '{intLand}'");
            if (!registry.EntitiesById.ContainsKey($"npc:{intLand}:sailor#intro_npc04{(suffix.Length > 0 ? "_" + suffix : "")}"))
                missing.Add($"NPC(Sailor) missing for '{intLand}'");
            if (!registry.EntitiesById.ContainsKey($"warp:{intLand}:intro_to_izlude{introToIzludeSuffix}"))
                missing.Add($"Warp(#intro_to_izlude) missing for '{intLand}'");
            if (!registry.GetNavigationAt(izInt, 18, 26).Any())
                missing.Add($"Navigation(intro_start) missing for '{izInt}'");
            if (!registry.GetNavigationAt(izInt, 51, 30).Any())
                missing.Add($"Navigation(intro_evt02) missing for '{izInt}'");
            if (!GeneratedMobSpawnRegistry.GetForMap(intLand).Any(s => s.Mob.AegisName == "G_PORING"))
                missing.Add($"MobSpawn(G_PORING) missing for '{intLand}'");
            if (!world.MonsterSpawns.Any(spawn => spawn.Map == intLand))
                missing.Add($"Composed monster instances missing for '{intLand}'");
        }

        Assert.True(missing.Count == 0, "Incomplete tutorial family coverage:\n" + string.Join('\n', missing));
    }

    // Explicit per-kind family counts, so a future regression narrows down immediately to WHICH
    // kind regressed (as the report format the original bug investigation used: "NPC family = 5/5,
    // warp family = 5/5, navigation family = 5/5, mob family = 4/5").
    [Fact]
    public void FamilyCoverageCounts_AreAllFiveOutOfFive()
    {
        var registry = WorldMapRegistry.Tutorial;

        var woundedSwordsman687Count = IzIntSuffixes.Count(suffix => registry.EntitiesById.ContainsKey($"npc:iz_int{suffix}:wounded swordsman#intro_npc01_iz_int{suffix}"));
        var woundedSwordsman688Count = IzIntSuffixes.Count(suffix => registry.EntitiesById.ContainsKey($"npc:iz_int{suffix}:wounded swordsman#intro_npc02_iz_int{suffix}"));
        var shipOutCount = IzIntSuffixes.Count(suffix => registry.EntitiesById.ContainsKey($"warp:iz_int{suffix}:ship_out{suffix}"));
        var captainCaroccCount = IzIntSuffixes.Count(suffix => registry.EntitiesById.ContainsKey($"npc:int_land{suffix}:captain carocc#intro_npc03{(suffix.Length > 0 ? "_" + suffix : "")}"));
        var luminCount = IzIntSuffixes.Count(suffix => registry.EntitiesById.ContainsKey($"npc:int_land{suffix}:lumin#new_ship{suffix}"));
        var sailorCount = IzIntSuffixes.Count(suffix => registry.EntitiesById.ContainsKey($"npc:int_land{suffix}:sailor#intro_npc04{(suffix.Length > 0 ? "_" + suffix : "")}"));
        var introToIzludeCount = IzIntSuffixes.Count(suffix =>
        {
            var introToIzludeSuffix = suffix switch { "" => "", "01" => "_a", "02" => "_b", "03" => "_c", "04" => "_d", _ => throw new ArgumentOutOfRangeException(nameof(suffix)) };
            return registry.EntitiesById.ContainsKey($"warp:int_land{suffix}:intro_to_izlude{introToIzludeSuffix}");
        });
        var navigationCount = IzIntSuffixes.Count(suffix => registry.GetNavigationAt("iz_int" + suffix, 18, 26).Any() && registry.GetNavigationAt("iz_int" + suffix, 51, 30).Any());
        var mobSpawnCount = IzIntSuffixes.Count(suffix => GeneratedMobSpawnRegistry.GetForMap("int_land" + suffix).Any(s => s.Mob.AegisName == "G_PORING"));

        Assert.Equal(5, woundedSwordsman687Count);
        Assert.Equal(5, woundedSwordsman688Count);
        Assert.Equal(5, shipOutCount);
        Assert.Equal(5, captainCaroccCount);
        Assert.Equal(5, luminCount);
        Assert.Equal(5, sailorCount);
        Assert.Equal(5, introToIzludeCount);
        Assert.Equal(5, navigationCount);
        Assert.Equal(5, mobSpawnCount);
    }
}
