using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.World;

public sealed class WorldMapRegistryTests
{
    [Fact]
    public void DefaultWorld_IsTheIntentionalGeneratedVerticalSlice()
    {
        var registry = WorldMapRegistry.Tutorial;
        // 13 = the original 10 Academy same-map doors (#room_in/#room_out x 5 iz_int variants) plus
        // 3 travel-corridor doors (izlude-prontera-travel-trace.txt): izlude_d<->prt_fild08d (2,
        // one per direction) and prt_fild08d->prontera (1, one-way in pinned source - see
        // ai/world-data.md).
        Assert.Equal(4468, registry.StaticWarpCount);
        // 35 = the full 5-map tutorial family (base iz_int/int_land + 01..04), not just the 01..04
        // instanced variants: 2 Wounded Swordsman states x 5 maps (iz_int/01/02/03/04) + Captain
        // Carocc x 5 (int_land/01/02/03/04) + Lumin x 5 + Sailor x 5 (int_land/01/02/03/04) +
        // #ship_out x 5 (iz_int/01/02/03/04) + #intro_to_izlude x 5 (int_land/01/02/03/04) =
        // 10+5+5+5+5+5 = 35. Every generic/base placement (iz_int/int_land, not just the *0N
        // instanced duplicates) must be present here - see WorldMapRegistryFamilyTests for the
        // per-suffix breakdown this count summarizes.
        // 61 = the original 35 Academy entities plus 26 travel-corridor entities
        // (izlude-prontera-travel-trace.txt, route-critical + low-cost presence only): Sailor x5
        // (izlude/izlude_a/b/c/d) + Guide#01/02izlude x5 maps = 10 + Resting Adventurer x5
        // (prt_fild08/a/b/c/d) + Guide#01..05prontera x5 + Karian#cmd9 x1 = 5+5+1 = 26.
        Assert.Equal(61, registry.EntityCount);
        Assert.Equal(0, registry.DynamicWarpActorCount);
        Assert.Contains("npc:int_land03:captain carocc#intro_npc03_03", registry.EntitiesById.Keys);
        Assert.Contains("npc:int_land03:lumin#new_ship03", registry.EntitiesById.Keys);
        Assert.Contains("npc:int_land03:sailor#intro_npc04_03", registry.EntitiesById.Keys);
        Assert.Contains("npc:iz_int03:wounded swordsman#intro_npc01_iz_int03", registry.EntitiesById.Keys);
        Assert.Contains("npc:iz_int03:wounded swordsman#intro_npc02_iz_int03", registry.EntitiesById.Keys);
        Assert.Contains("warp:iz_int01:ship_out01", registry.EntitiesById.Keys);
        // Generic/base placements (the regression this task fixes) must also be present.
        Assert.Contains("npc:int_land:captain carocc#intro_npc03", registry.EntitiesById.Keys);
        Assert.Contains("npc:int_land:lumin#new_ship", registry.EntitiesById.Keys);
        Assert.Contains("npc:int_land:sailor#intro_npc04", registry.EntitiesById.Keys);
        Assert.Contains("npc:iz_int:wounded swordsman#intro_npc01_iz_int", registry.EntitiesById.Keys);
        Assert.Contains("npc:iz_int:wounded swordsman#intro_npc02_iz_int", registry.EntitiesById.Keys);
        Assert.Contains("warp:iz_int:ship_out", registry.EntitiesById.Keys);
        Assert.Contains("warp:int_land:intro_to_izlude", registry.EntitiesById.Keys);
        Assert.DoesNotContain("dev:int_land04:athena_test_npc", registry.EntitiesById.Keys);
        // Travel-corridor NPC presence (izlude-prontera-travel-trace.txt): route-critical/low-cost
        // static NPCs only, not every captured NPC - see ai/world-data.md.
        Assert.Contains("npc:izlude_d:sailor#izlude_d", registry.EntitiesById.Keys);
        Assert.Contains("npc:izlude_d:guide#01izlude_d", registry.EntitiesById.Keys);
        Assert.Contains("npc:izlude_d:guide#02izlude_d", registry.EntitiesById.Keys);
        Assert.Contains("npc:prt_fild08d:resting adventurer#izd", registry.EntitiesById.Keys);
        Assert.Contains("npc:prontera:guide#04prontera", registry.EntitiesById.Keys);
        Assert.Contains("npc:prontera:karian#cmd9", registry.EntitiesById.Keys);
    }

    [Fact]
    public void TravelCorridorWarps_MatchGeneratedPinnedSourceValues()
    {
        // This test verifies the GENERATED (pinned legacy/rathena) WarpDefinition data only - the
        // izlude_d<->prt_fild08d doors here are NOT known to diverge from any verified capture.
        // prt_fild08d -> prontera's own pinned value (156,26) is a KNOWN, documented divergence
        // from the verified stock-iRO capture (prontera-walking.pcapng frame 3246 proves (156,34))
        // - see IroWireCompatibilityTests for the compatibility-resolved runtime value this test
        // deliberately does NOT assert, and IroWireCompatibility's own doc comment for why the
        // generated value here stays an untouched, faithful reproduction of pinned source.
        var registry = WorldMapRegistry.Tutorial;
        // izlude-prontera-travel-trace.txt sections H/J: izlude_d <-> prt_fild08d.
        Assert.True(registry.TryFindWarp("izlude_d", 20, 98, out var izludeExit));
        Assert.Equal(("prt_fild08d", (ushort)367, (ushort)212), (izludeExit.DestinationMap, izludeExit.DestinationX, izludeExit.DestinationY));
        Assert.True(registry.TryFindWarp("prt_fild08d", 371, 212, out var fieldBackToIzlude));
        Assert.Equal(("izlude_d", (ushort)24, (ushort)98), (fieldBackToIzlude.DestinationMap, fieldBackToIzlude.DestinationX, fieldBackToIzlude.DestinationY));
        // Section J: prt_fild08d -> prontera (one-way in pinned source; no reverse door exists).
        // (156,26) is the PINNED value, matching legacy/rathena/npc/re/warps/fields/
        // prontera_fild.txt:105 exactly - it is deliberately NOT the capture-verified (156,34).
        Assert.True(registry.TryFindWarp("prt_fild08d", 170, 378, out var fieldToProntera));
        Assert.Equal(("prontera", (ushort)156, (ushort)26), (fieldToProntera.DestinationMap, fieldToProntera.DestinationX, fieldToProntera.DestinationY));
    }

    [Fact]
    public void GeneratedTutorialActorsAndNavigation_MatchPinnedInstance03Source()
    {
        var captain = WorldMapRegistry.Tutorial.EntitiesById["npc:int_land03:captain carocc#intro_npc03_03"].Actor!;
        Assert.Equal(("int_land03", (ushort)78, (ushort)103, (ushort)873), (captain.Map, captain.X, captain.Y, captain.Class));
        var lumin = WorldMapRegistry.Tutorial.EntitiesById["npc:int_land03:lumin#new_ship03"].Actor!;
        Assert.Equal(("int_land03", (ushort)73, (ushort)100), (lumin.Map, lumin.X, lumin.Y));
        var start = Assert.Single(WorldMapRegistry.Tutorial.GetNavigationAt("iz_int03", 18, 26));
        Assert.Equal(("iz_int03", (ushort)52, (ushort)30), (start.DestinationMap, start.DestinationX, start.DestinationY));
        var instance01Start = Assert.Single(WorldMapRegistry.Tutorial.GetNavigationAt("iz_int01", 18, 26));
        Assert.Equal((ushort)52, instance01Start.DestinationX);
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", 27, 30, out var instance01RoomOut));
        Assert.Equal("iz_int01", instance01RoomOut.DestinationMap);
        Assert.Contains(WorldMapRegistry.Tutorial.GetVisibleWarpActors("iz_int01", 56, 32), actor => actor.EntityId == "npc:iz_int01:wounded swordsman#intro_npc01_iz_int01");
    }

    [Fact]
    public void GeneratedIzIntRoomWarps_MatchPinnedDefinitions()
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int", 26, 30, out var roomOut));
        Assert.Equal(("iz_int", (ushort)51, (ushort)30), (roomOut.DestinationMap, roomOut.DestinationX, roomOut.DestinationY));
        Assert.Equal(57, roomOut.SourceLine);
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int", 48, 31, out var roomIn));
        Assert.Equal(("iz_int", (ushort)22, (ushort)30), (roomIn.DestinationMap, roomIn.DestinationX, roomIn.DestinationY));
        Assert.Equal(63, roomIn.SourceLine);
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int03", 27, 30, out var instanceRoomOut));
        Assert.Equal(("iz_int03", (ushort)51, (ushort)30), (instanceRoomOut.DestinationMap, instanceRoomOut.DestinationX, instanceRoomOut.DestinationY));
    }

    [Fact]
    public void Route_SelectsFirstIntersectedWarpRatherThanRegistryOrder()
    {
        var later = new WarpDefinition("later", "test", 8, 5, 0, 0, "later", 1, 1, true, "test", 1);
        var earlier = new WarpDefinition("earlier", "test", 3, 5, 0, 0, "earlier", 1, 1, true, "test", 2);
        var registry = new WorldMapRegistry([later, earlier]);
        Assert.True(registry.TryFindFirstWarpAlongRoute("test", 0, 5, 10, 5, out var intersection));
        Assert.Same(earlier, intersection.Warp);
        Assert.Equal((ushort)3, intersection.X);
    }

    [Fact]
    public void IntroToIzludeDuplicate_IsGeneratedExecutableOnTouch()
    {
        var entity = WorldMapRegistry.Tutorial.EntitiesById["warp:int_land04:intro_to_izlude_d"];
        Assert.Equal(new WorldActorComponent("#intro_to_izlude_d", "int_land04", 49, 57, 0, 45), entity.Actor);
        Assert.True(WorldMapRegistry.Tutorial.TryFindFirstScriptTouchEnterAlongRoute("int_land04", 54, 64, 49, 57, out var entered));
        Assert.Same(entity, entered.Binding.Entity);
        Assert.Single(WorldMapRegistry.Tutorial.GetVisibleWarpActors("int_land04", 49, 57), actor => actor.Name == "#intro_to_izlude_d");
    }

    [Fact]
    public void WoundedSwordsman_IsGeneratedOrdinaryNpcAndDeveloperNpcIsAbsent()
    {
        var entity = WorldMapRegistry.Tutorial.EntitiesById["npc:iz_int:wounded swordsman#intro_npc02_iz_int"];
        Assert.Equal(new WorldActorComponent("Wounded Swordsman#intro_npc02_iz_int", "iz_int", 56, 32, 3, 688, 4), entity.Actor);
        Assert.DoesNotContain(WorldMapRegistry.Tutorial.GetVisibleWarpActors("int_land04", 50, 59), actor => actor.Name == "Athena Test NPC");
    }

    [Fact]
    public void CustomScriptRegistration_RejectsDuplicateUnlessOverrideIsExplicit()
    {
        var generated = Assert.Single(GeneratedScriptRegistry.Entities, entity => entity.Id == "npc:iz_int:wounded swordsman#intro_npc02_iz_int");
        var registration = new GeneratedScriptRegistration(generated, "OnClick", static () => new FixtureScript());
        var builder = new NpcScriptRegistryBuilder().AddGenerated([registration]);
        Assert.Throws<InvalidOperationException>(() => builder.AddCustom(registration));
        var registry = builder.AddCustom(registration, explicitlyOverrideGenerated: true).Build();
        Assert.True(registry.TryCreate(generated.Id, "OnClick", out var script));
        Assert.IsType<FixtureScript>(script);
    }
}

file sealed class FixtureScript : INpcScript
{
    public Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
