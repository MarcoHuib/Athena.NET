// Handwritten Athena.NET custom development content.
// NOT generated from rAthena.
// Never modified by WorldDataImporter.
using Athena.Net.MapServer.Customs.World.Izlude.Scripts;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Customs.World.Izlude;

// Registers the Athena Test NPC - a development-only NPC exercising the SAME NpcDefinition/
// NpcPlacement/INpcScript runtime contracts as generated content (AGENTS.md/ai/map-server.md's
// "custom content uses the normal runtime types" rule). Only composed onto the live world when
// "customs.enabled: yes" is configured (see MapConfig.CustomsEnabled) - see
// CustomWorldRegistry.Register/MapServerWorld.Build.
//
// Placed on int_land04, near the Academy's Captain Carocc/Lumin/Sailor cluster. int_land04 was
// chosen over iz_int03 (this NPC's original placement) after a live authenticated client's real
// 0x02EB confirmed a selected/persisted character's LastMap actually resolves to int_land04 - the
// tutorial family's int_land side, not the iz_int spawn side - so an operator standing on their
// live character could not previously reach this NPC without a same-map warp.
//
// (76,92) is a cell a real authenticated client actually walked through in a live session (that
// session's own 0x035F/0x0087 movement log recorded a walk from spawn (77,89) through (74,94),
// confirming it is reachable/rendered ground, not merely collision-flagged), and was independently
// re-validated against the real pinned legacy/rathena/db/map_cache.dat through
// RathenaMapCacheReader/MapCollisionProvider (both IsTraversalCell and IsWalkable return true
// there). It is comfortably clear of every existing int_land04 actor and trigger (see
// AcademyWorld.cs/AcademyWarps.cs): Captain Carocc#intro_npc03_04 (78,103, Manhattan distance 13),
// Lumin#new_ship04 (73,100, distance 11), Sailor#intro_npc04_04 (58,69, distance 41), and the
// #intro_to_izlude_d warp trigger (49,57, radius 2,2, distance 62).
internal static class IzludeCustomWorld
{
    private const string DefinitionId = "custom:izlude:athena_test_npc";
    private const string NpcName = "Athena Test NPC";

    // A plain, already-supported generic NPC sprite (a standard rAthena male-villager class) that
    // is genuinely distinct from every sprite class already used anywhere in the generated Academy
    // world - AcademyNpcs.CaptainCarocc (873), Lumin (639), Sailor (100), and the Wounded Swordsman
    // variants (687/688) - so this NPC is visually distinguishable from every generated tutorial
    // actor rather than reusing an existing one (e.g. Sailor's 100).
    private const ushort SpriteClass = 117;

    public static readonly NpcDefinition AthenaTestNpc = new(
        DefinitionId,
        NpcName,
        [new NpcBehaviorBinding("OnClick", static () => new AthenaTestNpcOnClickScript())]);

    public static void Register(WorldRegistryBuilder builder)
    {
        builder.AddNpc(AthenaTestNpc,
        [
            new NpcPlacement(
                PlacementId: "custom:int_land04:athena test npc",
                DefinitionId: DefinitionId,
                NpcName: NpcName,
                Map: "int_land04",
                X: 76,
                Y: 92,
                Direction: 0,
                Class: SpriteClass,
                RadiusX: 0,
                RadiusY: 0),
        ]);
    }
}
