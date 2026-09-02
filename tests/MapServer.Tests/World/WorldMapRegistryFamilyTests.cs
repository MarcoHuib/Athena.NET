using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Regression coverage for the "generic iz_int is an incomplete tutorial variant" bug: a new
// character can start on any of iz_int/iz_int01/iz_int02/iz_int03/iz_int04 (see start_point
// config), so every member of this family must be a functionally equivalent tutorial slice - not
// just the *0N instanced duplicates, with the generic/base map silently missing placements that
// only ever existed via emission-selection exclusions in the compile-npc-world/compile-navigation
// regeneration commands (tools/WorldDataImporter/README.md, ai/world-data.md's "Regeneration"
// section). These tests read WorldMapRegistry.Tutorial - the actual compiled/generated output - as
// the source of truth; they do not re-derive pinned rAthena data by hand.
public sealed class WorldMapRegistryFamilyTests
{
    // "" is the generic/base map (iz_int/int_land); "01".."04" are the pinned instanced duplicates.
    public static TheoryData<string> Suffixes => new("", "01", "02", "03", "04");

    public static TheoryData<string, string, string> TravelVariants => new()
    {
        { "", "izlude", "prt_fild08" },
        { "01", "izlude_a", "prt_fild08a" },
        { "02", "izlude_b", "prt_fild08b" },
        { "03", "izlude_c", "prt_fild08c" },
        { "04", "izlude_d", "prt_fild08d" },
    };

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void WoundedSwordsman_FirstState_Class687_ExistsAndIsInitiallyVisible(string suffix)
    {
        var izInt = "iz_int" + suffix;
        var entityId = $"npc:{izInt}:wounded swordsman#intro_npc01_iz_int{suffix}";

        Assert.True(WorldMapRegistry.Tutorial.EntitiesById.TryGetValue(entityId, out var entity), $"Missing entity '{entityId}'");
        var actor = entity.Actor!;
        Assert.Equal(izInt, actor.Map);
        Assert.Equal((ushort)56, actor.X);
        Assert.Equal((ushort)32, actor.Y);
        Assert.Equal((ushort)687, actor.Class);
        // EffectState 0 = not cloaked - pinned #intro_npc01_iz_int's OnInit has no cloakonnpc()
        // call at all (only questinfo(...)), unlike its sibling below - so it must be visible by
        // default, matching the runtime bug report ("initial Wounded Swordsman not visible").
        Assert.Equal(0u, actor.EffectState);
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void WoundedSwordsman_SecondState_Class688_ExistsAndIsInitiallyCloaked(string suffix)
    {
        var izInt = "iz_int" + suffix;
        var entityId = $"npc:{izInt}:wounded swordsman#intro_npc02_iz_int{suffix}";

        Assert.True(WorldMapRegistry.Tutorial.EntitiesById.TryGetValue(entityId, out var entity), $"Missing entity '{entityId}'");
        var actor = entity.Actor!;
        Assert.Equal(izInt, actor.Map);
        Assert.Equal((ushort)56, actor.X);
        Assert.Equal((ushort)32, actor.Y);
        Assert.Equal((ushort)688, actor.Class);
        // EffectState 4 = cloaked - pinned #intro_npc02_iz_int's OnInit explicitly calls
        // cloakonnpc() after questinfo(...), so it must start hidden until the first NPC's OnClick
        // toggles visibility (cloakonnpcself / cloakoffnpcself).
        Assert.Equal(4u, actor.EffectState);
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void ShipOut_ExistsAndWarpsToTheCorrespondingIntLandMap(string suffix)
    {
        var izInt = "iz_int" + suffix;
        var entityId = $"warp:{izInt}:ship_out{suffix}";

        Assert.True(WorldMapRegistry.Tutorial.EntitiesById.TryGetValue(entityId, out var entity), $"Missing entity '{entityId}'");
        Assert.Equal(izInt, entity.Actor!.Map);
        Assert.Equal((ushort)56, entity.Actor.X);
        Assert.Equal((ushort)15, entity.Actor.Y);

        // The generated ShipOutOnTouchScript derives its destination from StrNpcInfo(2) (the NPC's
        // own instance name minus the leading '#') at runtime, not from a hardcoded per-suffix
        // branch - this assertion documents the ENTITY exists and is positioned correctly; the
        // runtime destination-derivation itself is covered by the generated-script test below.
        Assert.True(WorldMapRegistry.Tutorial.TryFindFirstScriptTouchEnterAlongRoute(izInt, 50, 15, 56, 15, out var entered));
        Assert.Same(entity, entered.Binding.Entity);
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void CaptainCarocc_ExistsOnTheCorrespondingIntLandMap(string suffix)
    {
        var intLand = "int_land" + suffix;
        var entityId = $"npc:{intLand}:captain carocc#intro_npc03{(suffix.Length > 0 ? "_" + suffix : "")}";

        Assert.True(WorldMapRegistry.Tutorial.EntitiesById.TryGetValue(entityId, out var entity), $"Missing entity '{entityId}'");
        Assert.Equal(intLand, entity.Actor!.Map);
        Assert.Equal((ushort)78, entity.Actor.X);
        Assert.Equal((ushort)103, entity.Actor.Y);
        Assert.Equal((ushort)873, entity.Actor.Class);
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void Lumin_ExistsOnTheCorrespondingIntLandMap(string suffix)
    {
        var intLand = "int_land" + suffix;
        var entityId = $"npc:{intLand}:lumin#new_ship{suffix}";

        Assert.True(WorldMapRegistry.Tutorial.EntitiesById.TryGetValue(entityId, out var entity), $"Missing entity '{entityId}'");
        Assert.Equal(intLand, entity.Actor!.Map);
        Assert.Equal((ushort)73, entity.Actor.X);
        Assert.Equal((ushort)100, entity.Actor.Y);
        Assert.Equal((ushort)639, entity.Actor.Class);
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void Sailor_ExistsOnTheCorrespondingIntLandMap(string suffix)
    {
        var intLand = "int_land" + suffix;
        var entityId = $"npc:{intLand}:sailor#intro_npc04{(suffix.Length > 0 ? "_" + suffix : "")}";

        Assert.True(WorldMapRegistry.Tutorial.EntitiesById.TryGetValue(entityId, out var entity), $"Missing entity '{entityId}'");
        Assert.Equal(intLand, entity.Actor!.Map);
        Assert.Equal((ushort)58, entity.Actor.X);
        Assert.Equal((ushort)69, entity.Actor.Y);
        Assert.Equal((ushort)100, entity.Actor.Class); // 4W_SAILOR
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void IntroToIzlude_ExistsOnTheCorrespondingIntLandMap(string suffix)
    {
        var intLand = "int_land" + suffix;
        // Pinned duplicate naming is NOT simply "_01".."_04" - it follows the pinned source's own
        // lettered duplicate names (#intro_to_izlude_a/_b/_c/_d for int_land01..04), while the
        // generic template on int_land itself carries no suffix at all.
        var duplicateSuffix = suffix switch { "" => "", "01" => "_a", "02" => "_b", "03" => "_c", "04" => "_d", _ => throw new ArgumentOutOfRangeException(nameof(suffix)) };
        var entityId = $"warp:{intLand}:intro_to_izlude{duplicateSuffix}";

        Assert.True(WorldMapRegistry.Tutorial.EntitiesById.TryGetValue(entityId, out var entity), $"Missing entity '{entityId}'");
        Assert.Equal(intLand, entity.Actor!.Map);
        Assert.Equal((ushort)49, entity.Actor.X);
        Assert.Equal((ushort)57, entity.Actor.Y);
    }

    [Theory]
    [MemberData(nameof(TravelVariants))]
    public void EveryConfiguredTutorialVariant_HasItsSourceBackedHostedTravelCorridor(
        string tutorialSuffix, string izludeMap, string fieldMap)
    {
        Assert.Contains("int_land" + tutorialSuffix, MapServerHostingScope.ServedMaps);
        Assert.Contains(izludeMap, MapServerHostingScope.ServedMaps);
        Assert.Contains(fieldMap, MapServerHostingScope.ServedMaps);
        var registry = WorldMapRegistry.Tutorial;
        Assert.True(registry.TryFindWarp(izludeMap, 20, 98, out var exit));
        Assert.Equal((fieldMap, (ushort)367, (ushort)212), (exit.DestinationMap, exit.DestinationX, exit.DestinationY));
        Assert.True(registry.TryFindWarp(fieldMap, 170, 378, out var toProntera));
        Assert.Equal("prontera", toProntera.DestinationMap);
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void Navigation_ExistsForIntroStartAndIntroEvt02(string suffix)
    {
        var izInt = "iz_int" + suffix;

        var start = Assert.Single(WorldMapRegistry.Tutorial.GetNavigationAt(izInt, 18, 26));
        Assert.Equal((izInt, (ushort)52, (ushort)30), (start.DestinationMap, start.DestinationX, start.DestinationY));

        var evt02 = Assert.Single(WorldMapRegistry.Tutorial.GetNavigationAt(izInt, 51, 30));
        var intLand = "int_land" + suffix;
        Assert.Equal((intLand, (ushort)75, (ushort)100), (evt02.DestinationMap, evt02.DestinationX, evt02.DestinationY));
    }

    // Pinned rAthena's tutorial deliberately starts a SECOND navigation
    // (iz_int#intro_evt02 -> int_land 75,100) that survives the #ship_out transition - the client
    // is expected to keep showing that arrow until it reaches int_land 75,100, not until the map
    // changes. This test proves Athena does NOT synthesize a THIRD navigation once int_land itself
    // loads (MapClientSession's CzNotifyActorInit handler calls GetNavigationAt(_mapName, _x, _y)
    // again on every map load, including int_land - AcademyNavigation.All has zero int_land
    // entries, so that lookup is empty there): the "arrows outside the first room" observation is
    // consistent with pinned source re-showing the SAME second navigation, not a
    // duplicated/accumulated one. See ai/iro-2026-wire.md for the one proven 0x08E2 wire fact
    // (ground-arrow rendering); no capture evidence exists for a cancel/clear packet, so none is
    // invented here.
    [Theory]
    [MemberData(nameof(Suffixes))]
    public void NoThirdNavigation_IsSynthesizedWhenIntLandItselfLoads(string suffix)
    {
        var intLand = "int_land" + suffix;

        Assert.Empty(WorldMapRegistry.Tutorial.GetNavigationAt(intLand, 75, 100));
        Assert.Empty(WorldMapRegistry.Tutorial.GetNavigationAt(intLand, 85, 107));
    }

    // Proves no member of the configured start-point family has a silently truncated route: every
    // generic/instanced variant must have its full NPC/warp/navigation set, not just the *0N
    // duplicates a prior emission-selection invocation happened to include.
    [Theory]
    [MemberData(nameof(Suffixes))]
    public void FullTutorialSlice_IsPresentForEveryStartPointFamilyMember(string suffix)
    {
        var izInt = "iz_int" + suffix;
        var intLand = "int_land" + suffix;
        var duplicateSuffix = suffix switch { "" => "", "01" => "_a", "02" => "_b", "03" => "_c", "04" => "_d", _ => throw new ArgumentOutOfRangeException(nameof(suffix)) };

        Assert.Contains($"npc:{izInt}:wounded swordsman#intro_npc01_iz_int{suffix}", WorldMapRegistry.Tutorial.EntitiesById.Keys);
        Assert.Contains($"npc:{izInt}:wounded swordsman#intro_npc02_iz_int{suffix}", WorldMapRegistry.Tutorial.EntitiesById.Keys);
        Assert.Contains($"warp:{izInt}:ship_out{suffix}", WorldMapRegistry.Tutorial.EntitiesById.Keys);
        Assert.Contains($"npc:{intLand}:captain carocc#intro_npc03{(suffix.Length > 0 ? "_" + suffix : "")}", WorldMapRegistry.Tutorial.EntitiesById.Keys);
        Assert.Contains($"npc:{intLand}:lumin#new_ship{suffix}", WorldMapRegistry.Tutorial.EntitiesById.Keys);
        Assert.Contains($"warp:{intLand}:intro_to_izlude{duplicateSuffix}", WorldMapRegistry.Tutorial.EntitiesById.Keys);
        Assert.NotEmpty(WorldMapRegistry.Tutorial.GetNavigationAt(izInt, 18, 26));
        Assert.NotEmpty(WorldMapRegistry.Tutorial.GetNavigationAt(izInt, 51, 30));
    }

    // End-to-end integration test for the SPECIFIC route observed broken at runtime: a character
    // starting on the generic (suffixless) iz_int must be able to walk the full
    // iz_int -> Wounded Swordsman -> #ship_out -> int_land route, exactly like every *0N variant.
    [Fact]
    public void GenericIzIntRoute_WoundedSwordsmanThenShipOutThenIntLand_IsFullyPresent()
    {
        var registry = WorldMapRegistry.Tutorial;

        // Step 1: the Wounded Swordsman (both states) exist and are visible/reachable on iz_int.
        Assert.Contains(registry.GetVisibleWarpActors("iz_int", 56, 32), actor => actor.EntityId == "npc:iz_int:wounded swordsman#intro_npc01_iz_int");
        var visibleSwordsman = registry.EntitiesById["npc:iz_int:wounded swordsman#intro_npc01_iz_int"];
        Assert.Equal(0u, visibleSwordsman.Actor!.EffectState);
        var cloakedSwordsman = registry.EntitiesById["npc:iz_int:wounded swordsman#intro_npc02_iz_int"];
        Assert.Equal(4u, cloakedSwordsman.Actor!.EffectState);

        // Step 2: #ship_out exists on iz_int and is a touch-triggerable executable warp.
        var shipOut = registry.EntitiesById["warp:iz_int:ship_out"];
        Assert.True(registry.TryFindFirstScriptTouchEnterAlongRoute("iz_int", 50, 15, 56, 15, out var entered));
        Assert.Same(shipOut, entered.Binding.Entity);

        // Step 3: the destination int_land itself has its own Captain Carocc, Lumin, and
        // #intro_to_izlude - the route does not dead-end immediately after ship_out.
        Assert.Contains("npc:int_land:captain carocc#intro_npc03", registry.EntitiesById.Keys);
        Assert.Contains("npc:int_land:lumin#new_ship", registry.EntitiesById.Keys);
        Assert.Contains("warp:int_land:intro_to_izlude", registry.EntitiesById.Keys);
    }
}
