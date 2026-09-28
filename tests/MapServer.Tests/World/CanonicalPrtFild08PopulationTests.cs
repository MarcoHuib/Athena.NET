using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Locks the intended population of the ONE canonical prt_fild08 (the shared post-tutorial field every
// character now lands on; the channel copies prt_fild08a..d no longer host anything).
//
// Classification of the 51 pinned declarations that target prt_fild08 (source file -> load class):
//   npc/re/mobs/fields/prontera.txt        21 decl / 271 mobs  RenewalDefault  (Renewal field population)
//   npc/re/mobs/championmobs.txt            5 decl /   5 mobs  RenewalDefault  (champion variants, active in the pinned Renewal graph)
//   npc/re/mobs/academy.txt                 4 decl / 340 mobs  AthenaOverlay   (explicit Athena tutorial-content policy; the pinned
//                                                                               file lists the base map alongside the a..d copies)
//   npc/pre-re/mobs/fields/prontera.txt     4 decl / 140 mobs  PreRenewalSource (inactive)
//   npc/events/{RWC_2011,StPatrick_2008,christmas_2008,christmas_2013,dumplingfestival,halloween_2006,
//               halloween_2013,xmas}.txt   17 decl /  ~82 mobs Disabled        (event content stays independent: represented, not active)
// Effective (AthenaIroEffective = RenewalDefault + AthenaOverlay): 30 declarations, 616 monsters. The old
// live values (base 616 vs prt_fild08c 340) were simply "everything effective for that map id": the base
// map got the Renewal field set on top of the academy set, the copy only had the academy set.
public sealed class CanonicalPrtFild08PopulationTests
{
    private const string Map = "prt_fild08";
    private const string RenewalFields = "legacy/rathena/npc/re/mobs/fields/prontera.txt";
    private const string Champions = "legacy/rathena/npc/re/mobs/championmobs.txt";
    private const string Academy = "legacy/rathena/npc/re/mobs/academy.txt";
    private const string PreRenewalFields = "legacy/rathena/npc/pre-re/mobs/fields/prontera.txt";

    private static (int Declarations, int Monsters) Total(IEnumerable<MobSpawnDefinition> spawns) => (spawns.Count(), spawns.Sum(spawn => spawn.Count));
    private static IEnumerable<MobSpawnDefinition> From(IEnumerable<MobSpawnDefinition> spawns, string file) => spawns.Where(spawn => spawn.Source.File == file);

    [Fact]
    public void Registry_RepresentsEveryPinnedDeclaration_ByLoadClass()
    {
        var all = GeneratedMobSpawnRegistry.GetForMap(Map);

        Assert.Equal(51, all.Count);
        Assert.Equal((21, 271), Total(From(all, RenewalFields)));
        Assert.Equal((5, 5), Total(From(all, Champions)));
        Assert.Equal((4, 340), Total(From(all, Academy)));
        Assert.Equal((4, 140), Total(From(all, PreRenewalFields)));
        var events = all.Where(spawn => spawn.Source.File.StartsWith("legacy/rathena/npc/events/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(17, events.Length);
        Assert.Equal(8, events.Select(spawn => spawn.Source.File).Distinct().Count());
        Assert.Equal(51, 21 + 5 + 4 + 4 + events.Length);
    }

    [Fact]
    public void EffectiveProfile_IsRenewalFieldsPlusChampionsPlusTheAthenaAcademyOverlay_Exactly()
    {
        var effective = GeneratedMobSpawnLoadProfiles.GetForMap(Map, MobSpawnLoadProfile.AthenaIroEffective);

        Assert.Equal((30, 616), Total(effective));
        Assert.Equal(new[] { Academy, Champions, RenewalFields }, effective.Select(spawn => spawn.Source.File).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal((21, 271), Total(From(effective, RenewalFields)));
        Assert.Equal((5, 5), Total(From(effective, Champions)));
        Assert.Equal((4, 340), Total(From(effective, Academy)));

        // Renewal-only view (no Athena overlay): the academy set is the ONLY difference.
        var renewal = GeneratedMobSpawnLoadProfiles.GetForMap(Map, MobSpawnLoadProfile.RathenaRenewalDefault);
        Assert.Equal((26, 276), Total(renewal));
        Assert.DoesNotContain(renewal, spawn => spawn.Source.File == Academy);
    }

    [Fact]
    public void InactiveClasses_NeverReachTheEffectiveProfile_AndEventsStayIndependent()
    {
        var effective = GeneratedMobSpawnLoadProfiles.GetForMap(Map, MobSpawnLoadProfile.AthenaIroEffective);

        Assert.DoesNotContain(effective, spawn => spawn.Source.File == PreRenewalFields);                                       // pre-re: inactive
        Assert.DoesNotContain(effective, spawn => spawn.Source.File.Contains("/npc/events/", StringComparison.Ordinal));        // events: disabled, not permanently on
        // ...and not permanently off either: the declarations stay represented for whichever mechanism later activates them.
        Assert.Contains(GeneratedMobSpawnRegistry.GetForMap(Map), spawn => spawn.Source.File == "legacy/rathena/npc/events/christmas_2013.txt");
        Assert.Contains(GeneratedMobSpawnRegistry.GetForMap(Map), spawn => spawn.Source.File == "legacy/rathena/npc/events/halloween_2013.txt");
    }

    [Fact]
    public void KeyMobCounts_OfTheCanonicalPopulation()
    {
        var effective = GeneratedMobSpawnLoadProfiles.GetForMap(Map, MobSpawnLoadProfile.AthenaIroEffective);
        int Count(string aegis) => effective.Where(spawn => spawn.Mob.AegisName == aegis).Sum(spawn => spawn.Count);

        Assert.Equal(197, Count("PORING"));        // 87 Renewal + 110 academy
        Assert.Equal(167, Count("LUNATIC"));       // 67 Renewal + 100 academy
        Assert.Equal(177, Count("FABRE"));         // 77 Renewal + 100 academy
        Assert.Equal(50, Count("LITTLE_PORING"));  // 20 Renewal + 30 academy
        Assert.Equal(20, Count("PUPA"));
    }

    [Fact]
    public void RuntimeActivation_InstantiatesExactlyTheCanonicalPopulation_OnTheCanonicalMapOnly()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);

        var onCanonical = world.MonsterSpawns.Where(spawn => string.Equals(spawn.Map, Map, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal((30, 616), Total(onCanonical));
        Assert.DoesNotContain(world.MonsterSpawns, spawn => spawn.Map.StartsWith("prt_fild08", StringComparison.OrdinalIgnoreCase) && !string.Equals(spawn.Map, Map, StringComparison.OrdinalIgnoreCase));
    }
}
