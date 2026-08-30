using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Structural/API coverage for the complete generated mob-spawn registry (ai/world-data.md's
// "Generated mob spawns" section, task: "generate all pinned rAthena mob spawns into Athena.NET
// production world data"). The analyzer-parity/invalid-dependency/source-scan regression tests
// live in WorldDataImporter.Tests.MobSpawnGenerationTests instead (that project cannot reference
// this compiled generated output - see its own doc comment); this file covers the COMPILED
// GeneratedMobSpawnRegistry's own map-lookup contract and end-to-end runtime activation.
public sealed class GeneratedMobSpawnRegistryTests
{
    [Fact]
    public void All_ContainsExactly9844Declarations()
    {
        Assert.Equal(9844, GeneratedMobSpawnRegistry.All.Length);
        Assert.Equal(9844, GeneratedMobSpawnRegistry.Count);
    }

    // Task section 32: unknown map returns an empty collection, never throws.
    [Fact]
    public void GetForMap_UnknownMap_ReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(GeneratedMobSpawnRegistry.GetForMap("this_map_does_not_exist_anywhere"));
        Assert.False(GeneratedMobSpawnRegistry.TryGetMap("this_map_does_not_exist_anywhere", out var found));
        Assert.Empty(found);
    }

    [Fact]
    public void TryGetMap_KnownMap_ReturnsTrueAndTheExpectedDeclarations()
    {
        Assert.True(GeneratedMobSpawnRegistry.TryGetMap("int_land", out var spawns));
        var gPoring = Assert.Single(spawns, spawn => spawn.Mob.AegisName == "G_PORING");
        Assert.Equal(40, gPoring.Count);
    }

    // Every generated spawn's Mob reference resolves through GeneratedMobRegistry (task section 10) -
    // MobSpawnDefinition.Mob is a direct MobDefinition reference (compile-time
    // GeneratedMobs.<Symbol>), so existing in the registry already proves resolution; this
    // additionally confirms the referenced Id is a real GeneratedMobRegistry key (never a
    // definition that drifted out of sync with the registry it should be part of).
    [Fact]
    public void EveryMobReference_ResolvesThroughGeneratedMobRegistry()
    {
        Assert.All(GeneratedMobSpawnRegistry.All, spawn => Assert.True(GeneratedMobRegistry.TryGet(spawn.Mob.Id, out var resolved) && ReferenceEquals(resolved, spawn.Mob)));
    }

    // Exact invalid-dependency regression (task section 40): the three known evt_zombie
    // declarations remain generated but are absent from any composed world built with
    // MapServerHostingScope.ServedMaps (evt_zombie is never a served/valid map) - proving "generated
    // but not runtime-activated" end to end, not merely by absence-of-evidence.
    [Fact]
    public void EvtZombieDeclarations_AreGeneratedButNeverInstantiatedInAServedWorld()
    {
        var evtZombieSpawns = GeneratedMobSpawnRegistry.All.Where(spawn => spawn.Map == "evt_zombie").OrderBy(spawn => spawn.Source.Line).ToArray();
        Assert.Equal(3, evtZombieSpawns.Length);
        Assert.Equal([267, 268, 269], evtZombieSpawns.Select(spawn => spawn.Source.Line));
        Assert.DoesNotContain("evt_zombie", MapServerHostingScope.ServedMaps);

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), servedMaps: MapServerHostingScope.ServedMaps);
        Assert.DoesNotContain(world.Monsters.AllInstances, instance => instance.Map == "evt_zombie");
    }

    // Task section 25/26: proves the complete pipeline end-to-end for a real map that previously
    // had ZERO hand-generated production spawn slice - "pay_fild01" (npc/re/mobs/fields/payon.txt),
    // never served by MapServerHostingScope and never part of the old hand-picked
    // AcademyMobSpawns/PrtFild08MobSpawns files this branch retired. Uses MapServerWorld.Build with
    // an EXPLICIT servedMaps set containing only this one map (never mutating the production
    // MapServerHostingScope.ServedMaps itself - task section 14: runtime activation must follow the
    // map lifecycle, not blindly instantiate all 9,841 valid declarations across every map at once)
    // to prove: rAthena source -> generated spawn -> generated MobDefinition -> real MonsterRegistry
    // instance, through the exact same runtime MonsterRegistry/MonsterRuntime every other map uses.
    [Fact]
    public void RuntimeActivation_PreviouslyUnservedRealMap_InstantiatesThroughTheGenericPipeline()
    {
        const string Map = "pay_fild01";
        Assert.DoesNotContain(Map, MapServerHostingScope.ServedMaps);

        var generatedSpawns = GeneratedMobSpawnRegistry.GetForMap(Map);
        Assert.NotEmpty(generatedSpawns);
        Assert.Contains(generatedSpawns, spawn => spawn.Mob.AegisName == "WILOW" && spawn.Count == 181);
        Assert.Contains(generatedSpawns, spawn => spawn.Mob.AegisName == "PORING" && spawn.Count == 38);
        Assert.Contains(generatedSpawns, spawn => spawn.Mob.AegisName == "FABRE" && spawn.Count == 38);

        var servedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Map };
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), servedMaps: servedMaps);

        var instances = world.Monsters.AllInstances.Where(instance => instance.Map == Map).ToArray();
        Assert.Equal(generatedSpawns.Sum(spawn => spawn.Count), instances.Length);
        Assert.All(instances, instance => Assert.True(instance.IsAlive));
        Assert.Contains(instances, instance => instance.Spawn.Mob.AegisName == "WILOW");

        // Only the ONE served map's declarations were instantiated - definition availability stays
        // global (GeneratedMobSpawnRegistry.All has thousands more), but runtime instantiation
        // follows the explicit servedMaps set exactly.
        Assert.All(world.Monsters.AllInstances, instance => Assert.Equal(Map, instance.Map, ignoreCase: true));
    }

    // No source declaration exists in more than one generated array - the physical
    // map/family-oriented layout must still produce exactly one canonical representation per
    // declaration (source file + line, the stable identity - task's "no source identity exists in
    // more than one generated spawn file"), even though several source files can contribute to the
    // same map's single array (task section 42/43).
    [Fact]
    public void NoSourceDeclarationIsDuplicated()
    {
        var identities = GeneratedMobSpawnRegistry.All.Select(spawn => (spawn.Source.File, spawn.Source.Line)).ToArray();
        Assert.Equal(identities.Length, identities.Distinct().Count());
    }

    // Existing world-family folders are reused, never duplicated as new PrtFild08A/PrtFild08B/...
    // folders (the retired hand-authored PrtFild08MobSpawns.cs precedent this restores) - PrtFild08
    // is the canonical example named explicitly by this correction.
    [Fact]
    public void PrtFild08Family_IsOneModuleCoveringAllFiveConcreteMaps()
    {
        Assert.True(GeneratedMobSpawnRegistry.TryGetMap("prt_fild08", out var baseMap));
        Assert.True(GeneratedMobSpawnRegistry.TryGetMap("prt_fild08a", out var a));
        Assert.True(GeneratedMobSpawnRegistry.TryGetMap("prt_fild08b", out var b));
        Assert.True(GeneratedMobSpawnRegistry.TryGetMap("prt_fild08c", out var c));
        Assert.True(GeneratedMobSpawnRegistry.TryGetMap("prt_fild08d", out var d));

        Assert.NotEmpty(baseMap); Assert.NotEmpty(a); Assert.NotEmpty(b); Assert.NotEmpty(c); Assert.NotEmpty(d);

        // Every one of these five maps' declarations is drawn from the SAME physical array set
        // (PrtFild08Spawn.All) - proven indirectly: the union of the five per-map lookups accounts
        // for every "prt_fild08*" entry in the flattened registry, with no leftover and no overlap.
        var expectedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "prt_fild08", "prt_fild08a", "prt_fild08b", "prt_fild08c", "prt_fild08d" };
        var allPrtFild08Entries = GeneratedMobSpawnRegistry.All.Where(spawn => expectedMaps.Contains(spawn.Map)).ToArray();
        var lookedUp = baseMap.Concat(a).Concat(b).Concat(c).Concat(d).ToArray();
        Assert.Equal(allPrtFild08Entries.Length, lookedUp.Length);
    }

    // prt_fild08d's complete real population (task: "must include every source declaration
    // targeting prt_fild08d, including event contributions" - not the historical hand-picked
    // academy.txt-only subset of 340). Christmas 2013 (Smokey Gift/Sock) and Halloween 2013
    // (Organic/Inorganic Jakk) event content also targets this exact map, discovered only once
    // generation scanned every pinned source file instead of one hand-picked one.
    [Fact]
    public void PrtFild08d_ContainsCompleteRealPopulationAcrossAllContributingSourceFiles()
    {
        Assert.True(GeneratedMobSpawnRegistry.TryGetMap("prt_fild08d", out var spawns));
        var sourceFiles = spawns.Select(spawn => spawn.Source.File).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("legacy/rathena/npc/re/mobs/academy.txt", sourceFiles);
        Assert.Contains("legacy/rathena/npc/events/christmas_2013.txt", sourceFiles);
        Assert.Contains("legacy/rathena/npc/events/halloween_2013.txt", sourceFiles);
        // 8 distinct source DECLARATIONS (task section 6: a declaration with Count=N stays one
        // source-backed spawn definition, never expanded into N generated rows); their Count fields
        // sum to 355 total simultaneously-managed monster instances at runtime.
        Assert.Equal(8, spawns.Count);
        Assert.Equal(355, spawns.Sum(spawn => spawn.Count));
        Assert.Equal(110, spawns.Single(spawn => spawn.Mob.AegisName == "PORING").Count);
        Assert.Equal(100, spawns.Single(spawn => spawn.Mob.AegisName == "LUNATIC").Count);
        Assert.Equal(100, spawns.Single(spawn => spawn.Mob.AegisName == "FABRE").Count);
        Assert.Equal(30, spawns.Single(spawn => spawn.Mob.AegisName == "LITTLE_PORING").Count);
        Assert.Equal(5, spawns.Single(spawn => spawn.Mob.AegisName == "XMAS_SMOKEY_GIFT").Count);
        Assert.Equal(5, spawns.Single(spawn => spawn.Mob.AegisName == "XMAS_SMOKEY_SOCK").Count);
        Assert.Equal(1, spawns.Single(spawn => spawn.Mob.AegisName == "ORGANIC_JAKK").Count);
        Assert.Equal(4, spawns.Single(spawn => spawn.Mob.AegisName == "INORGANIC_JAKK").Count);
    }

    // The three known evt_zombie declarations are generated under the dedicated Events/EvtZombie
    // organizational module (task's classification rule: source under npc/events/ AND a map token
    // starting with "evt_") - present in GeneratedMobSpawnRegistry.All (source coverage preserved),
    // but this placement is organizational ONLY and must never imply evt_zombie is a valid loaded
    // map (task's explicit "Event classification does NOT make the map runtime-valid" rule).
    [Fact]
    public void EvtZombieDeclarations_AreClassifiedUnderTheEventsModule_ButRemainRuntimeInvalid()
    {
        var evtZombieSpawns = GeneratedMobSpawnRegistry.All.Where(spawn => spawn.Map == "evt_zombie").OrderBy(spawn => spawn.Source.Line).ToArray();
        Assert.Equal(3, evtZombieSpawns.Length);
        Assert.Equal([267, 268, 269], evtZombieSpawns.Select(spawn => spawn.Source.Line));
        Assert.All(evtZombieSpawns, spawn => Assert.Equal("legacy/rathena/npc/events/halloween_2008.txt", spawn.Source.File));

        // Every generated mob reference still resolves (source declaration valid, mob reference
        // valid) even though the map dependency itself is unresolved.
        Assert.All(evtZombieSpawns, spawn => Assert.True(GeneratedMobRegistry.TryGet(spawn.Mob.Id, out var resolved) && ReferenceEquals(resolved, spawn.Mob)));

        // Never a valid/served map - runtime activation stays disabled regardless of the
        // organizational Events/ placement used to generate these three declarations.
        Assert.DoesNotContain("evt_zombie", MapServerHostingScope.ServedMaps);
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), servedMaps: MapServerHostingScope.ServedMaps);
        Assert.DoesNotContain(world.Monsters.AllInstances, instance => instance.Map == "evt_zombie");
    }
}
