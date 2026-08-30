using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.MobSpawns;
using Athena.Net.MapServer.Generated.GameData.Mobs;
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
}
