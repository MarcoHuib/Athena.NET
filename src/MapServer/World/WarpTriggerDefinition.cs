using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.World;

// Mirrors NpcDefinition/NpcPlacement exactly, for the rAthena WARPNPC script+duplicate() pattern
// (e.g. #ship_out/#ship_out01-04, #intro_to_izlude/#intro_to_izlude_a-d): one shared OnTouch behavior,
// N independently placed instances. Distinct from plain declarative `warp` directives (WarpDefinition),
// which have no shared behavior to extract and are unaffected by this type.
public sealed record WarpTriggerDefinition(
    string DefinitionId,
    string TemplateNpcName,
    NpcBehaviorBinding OnTouch,
    WorldSourceInfo? Source = null);

public sealed record WarpTriggerPlacement(
    string PlacementId,
    string DefinitionId,
    string NpcName,
    string Map,
    ushort X,
    ushort Y,
    byte Direction,
    ushort RadiusX,
    ushort RadiusY);
