using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.World;

public sealed record NpcDefinition(
    string DefinitionId,
    string TemplateNpcName,
    IReadOnlyList<NpcBehaviorBinding> Behaviors,
    WorldSourceInfo? Source = null);

public sealed record NpcBehaviorBinding(string Trigger, Func<INpcScript> ScriptFactory);

public sealed record NpcPlacement(
    string PlacementId,
    string DefinitionId,
    string NpcName,
    string Map,
    ushort X,
    ushort Y,
    byte Direction,
    ushort Class,
    ushort RadiusX,
    ushort RadiusY,
    uint InitialEffectState = 0);
