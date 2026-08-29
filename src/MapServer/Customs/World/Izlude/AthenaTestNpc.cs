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
// Placed on iz_int03, the tutorial instance new characters actually spawn on (see
// ai/map-server.md's "Map-state diagnosis" section: the configured start_point list resolves new
// characters to iz_int03 (18,26)). (15,22) is a handful of cells from spawn, on open ground, and
// does not overlap: the #room_out03/#room_in03 warp doors at (27,30)/(47,30) (radius 1,1 each,
// see AcademyWarps.cs), or the Wounded Swordsman NPCs at (56,32) (radius 5,5, see AcademyWorld.cs).
internal static class IzludeCustomWorld
{
    private const string DefinitionId = "custom:izlude:athena_test_npc";
    private const string NpcName = "Athena Test NPC";

    // A plain, already-supported NPC sprite - the same class id AcademyNpcs.Sailor already uses,
    // so no new client resource is required.
    private const ushort SpriteClass = 100;

    public static readonly NpcDefinition AthenaTestNpc = new(
        DefinitionId,
        NpcName,
        [new NpcBehaviorBinding("OnClick", static () => new AthenaTestNpcOnClickScript())]);

    public static void Register(WorldRegistryBuilder builder)
    {
        builder.AddNpc(AthenaTestNpc,
        [
            new NpcPlacement(
                PlacementId: "custom:iz_int03:athena test npc",
                DefinitionId: DefinitionId,
                NpcName: NpcName,
                Map: "iz_int03",
                X: 15,
                Y: 22,
                Direction: 0,
                Class: SpriteClass,
                RadiusX: 0,
                RadiusY: 0),
        ]);
    }
}
