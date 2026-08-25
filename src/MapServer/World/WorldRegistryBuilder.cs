using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.World;

// Aggregation entry point for a world's content, generic across content kinds.
// AddNpc is the first content family; future kinds (monster spawns, shop placements, etc.)
// are expected to gain sibling AddX methods on this same builder rather than parallel types.
public sealed record WorldRegistryBuildResult(IReadOnlyList<WorldEntityDefinition> Entities, NpcScriptRegistry Scripts, IReadOnlyList<MobSpawnDefinition> MobSpawns);

public sealed class WorldRegistryBuilder
{
    private readonly List<WorldEntityDefinition> _entities = [];
    private readonly NpcScriptRegistryBuilder _scripts = new();
    private readonly List<MobSpawnDefinition> _mobSpawns = [];

    // Monster spawns have no sprite/script/actor-name concept of their own at
    // build time (unlike AddNpc/AddWarpTrigger, which construct a
    // WorldEntityDefinition here) - a MobSpawnDefinition is pure generated
    // data; MonsterRegistry is what turns it into runtime WorldActor-shaped
    // MobInstance actors, using the SAME WorldActorIdAllocator MapServerWorld
    // hands to WorldMapRegistry, so monster/NPC/warp actor IDs share one
    // namespace instead of each content kind allocating its own.
    public WorldRegistryBuilder AddMobSpawn(MobSpawnDefinition spawn)
    {
        _mobSpawns.Add(spawn);
        return this;
    }

    public WorldRegistryBuilder AddNpc(NpcDefinition definition, IReadOnlyList<NpcPlacement> placements, bool explicitlyOverrideGenerated = false)
    {
        if (placements.Count == 0) throw new InvalidOperationException($"NpcDefinition '{definition.DefinitionId}' was added with zero placements.");
        foreach (var placement in placements)
        {
            if (!string.Equals(placement.DefinitionId, definition.DefinitionId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Placement '{placement.PlacementId}' references DefinitionId '{placement.DefinitionId}' but was added under definition '{definition.DefinitionId}'.");

            var isTemplatePlacement = string.Equals(placement.NpcName, definition.TemplateNpcName, StringComparison.Ordinal);
            var actor = new WorldActorComponent(placement.NpcName, placement.Map, placement.X, placement.Y, placement.Direction, placement.Class, placement.InitialEffectState);
            var scripts = definition.Behaviors
                .Select(behavior => new ScriptBehaviorDefinition(
                    behavior.Trigger, placement.Map, placement.X, placement.Y, placement.RadiusX, placement.RadiusY,
                    true, true, [], $"Generated from {definition.DefinitionId}",
                    null, isTemplatePlacement ? null : definition.TemplateNpcName))
                .ToArray();
            var entity = new WorldEntityDefinition(1, placement.PlacementId, "Npc", actor, [], scripts,
                definition.Source ?? new WorldSourceInfo("rAthena", "", "", 0));
            _entities.Add(entity);

            foreach (var behavior in definition.Behaviors)
                Add(new GeneratedScriptRegistration(entity, behavior.Trigger, behavior.ScriptFactory), explicitlyOverrideGenerated);
        }
        return this;
    }

    public WorldRegistryBuilder AddWarpTrigger(WarpTriggerDefinition definition, IReadOnlyList<WarpTriggerPlacement> placements, bool explicitlyOverrideGenerated = false)
    {
        if (placements.Count == 0) throw new InvalidOperationException($"WarpTriggerDefinition '{definition.DefinitionId}' was added with zero placements.");
        foreach (var placement in placements)
        {
            if (!string.Equals(placement.DefinitionId, definition.DefinitionId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Placement '{placement.PlacementId}' references DefinitionId '{placement.DefinitionId}' but was added under definition '{definition.DefinitionId}'.");

            var isTemplatePlacement = string.Equals(placement.NpcName, definition.TemplateNpcName, StringComparison.Ordinal);
            var actor = new WorldActorComponent(placement.NpcName, placement.Map, placement.X, placement.Y, placement.Direction, WorldActor.ClassId);
            var script = new ScriptBehaviorDefinition(
                definition.OnTouch.Trigger, placement.Map, placement.X, placement.Y, placement.RadiusX, placement.RadiusY,
                true, true, [], $"Generated from {definition.DefinitionId}",
                null, isTemplatePlacement ? null : definition.TemplateNpcName);
            var entity = new WorldEntityDefinition(1, placement.PlacementId, "Warp", actor, [], [script],
                definition.Source ?? new WorldSourceInfo("rAthena", "", "", 0));
            _entities.Add(entity);

            Add(new GeneratedScriptRegistration(entity, definition.OnTouch.Trigger, definition.OnTouch.ScriptFactory), explicitlyOverrideGenerated);
        }
        return this;
    }

    public WorldRegistryBuilder AddGeneratedRegistration(GeneratedScriptRegistration registration)
    {
        _entities.Add(registration.Entity);
        Add(registration, replace: false);
        return this;
    }

    public WorldRegistryBuildResult Build() => new(_entities.ToArray(), _scripts.Build(), _mobSpawns.ToArray());

    private void Add(GeneratedScriptRegistration registration, bool replace)
    {
        if (replace) _scripts.AddCustom(registration, explicitlyOverrideGenerated: true);
        else _scripts.AddGenerated([registration]);
    }
}
