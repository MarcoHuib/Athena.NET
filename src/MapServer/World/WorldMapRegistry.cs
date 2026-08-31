using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.Generated.World.Izlude.Academy;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.World;

public sealed class WorldMapRegistry
{
    private readonly IReadOnlyList<WarpDefinition> _warps;
    private readonly IReadOnlyList<WorldActor> _worldActors;
    private readonly IReadOnlyDictionary<string, WorldEntityDefinition> _entitiesById;
    private readonly IReadOnlyDictionary<uint, (WorldEntityDefinition Entity, ScriptBehaviorDefinition Script)> _interactionsByActorId;
    private readonly IReadOnlyList<ScriptTouchBinding> _touchScripts;
    private readonly int _dynamicWarpActorCount;
    private readonly IReadOnlyList<NavigationDefinition> _navigation;
    public NpcScriptRegistry Scripts { get; }

    public WorldMapRegistry(IEnumerable<WarpDefinition> warps, IEnumerable<WarpActorDefinition>? dynamicWarpActors = null)
        : this(warps, [], dynamicWarpActors) { }

    // `allocator` defaults to a fresh private instance, preserving every existing call site's
    // "each WorldMapRegistry owns its own NPC/warp actor-ID namespace" behavior (all current tests
    // and WorldMapRegistry.Tutorial rely on this). MapServerWorld.Build() is the one caller that
    // passes an explicit, SHARED allocator, so the composed live world's NPCs/warps and monsters
    // draw from the same ID namespace instead of two independently-numbered ones.
    internal WorldMapRegistry(IEnumerable<WarpDefinition> warps, IEnumerable<WorldEntityDefinition> entities, IEnumerable<WarpActorDefinition>? dynamicWarpActors = null, NpcScriptRegistry? scripts = null, WorldActorIdAllocator? allocator = null)
    {
        Scripts = scripts ?? GeneratedScriptRegistry.Registry;
        _navigation = GeneratedTutorialNavigation.All;
        _warps = warps.ToArray();
        _entitiesById = entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        allocator ??= new WorldActorIdAllocator();
        var dynamicActors = (dynamicWarpActors ?? []).ToArray();
        _dynamicWarpActorCount = dynamicActors.Length;
        var entityActorKeys = _entitiesById.Values.Where(entity => entity.Actor is not null).Select(entity => SemanticKey(entity.Actor!.Map, entity.Actor.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityActors = _entitiesById.Values.Where(entity => entity.Actor is not null).Select(ToActorDefinition).Where(actor => actor is not null).Select(actor => actor!);
        _worldActors = _warps.Where(warp => warp.HasWarpActor && warp.RadiusX <= byte.MaxValue && warp.RadiusY <= byte.MaxValue && !entityActorKeys.Contains(SemanticKey(warp.SourceMap, warp.Name)))
            .Select(warp => new WarpActorDefinition(warp.Name, warp.SourceMap, warp.SourceX, warp.SourceY, (byte)warp.RadiusX, (byte)warp.RadiusY))
            .Concat(entityActors)
            .Concat(dynamicActors)
            .Select(actor => new WorldActor(allocator.Allocate(), actor.Name.Length > 24 ? actor.Name[..24] : actor.Name, actor.MapName, actor.X, actor.Y, actor.RadiusX, actor.RadiusY, actor.SpriteClass, actor.Direction, actor.EffectState, actor.EntityId)).ToArray();
        _interactionsByActorId = _worldActors
            .Select(actor => (Actor: actor, Entity: actor.EntityId is not null && _entitiesById.TryGetValue(actor.EntityId, out var entity) ? entity : null))
            .Where(item => item.Entity is not null)
            .SelectMany(item => item.Entity!.Scripts.Where(script => script.RuntimeExecutable && string.Equals(script.Trigger, "OnClick", StringComparison.OrdinalIgnoreCase) &&
                (script.Instructions is { Count: > 0 } || Scripts.TryCreate(item.Entity.Id, script.Trigger, out _))).Select(script => (item.Actor.ActorId, item.Entity, Script: script)))
            .ToDictionary(item => item.ActorId, item => (item.Entity!, item.Script));
        _touchScripts = _worldActors
            .Select(actor => (Actor: actor, Entity: actor.EntityId is not null && _entitiesById.TryGetValue(actor.EntityId, out var entity) ? entity : null))
            .Where(item => item.Entity is not null)
            .SelectMany(item => item.Entity!.Scripts.Where(script => script.RuntimeExecutable && string.Equals(script.Trigger, "OnTouch", StringComparison.OrdinalIgnoreCase) &&
                (script.Instructions is { Count: > 0 } || Scripts.TryCreate(item.Entity.Id, script.Trigger, out _)))
                .Select(script => new ScriptTouchBinding(item.Entity!, item.Actor, script)))
            .OrderBy(item => item.Entity.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static WorldMapRegistry Tutorial { get; } = LoadGenerated();
    public int MapCount => _warps.Select(warp => warp.SourceMap).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int StaticWarpCount => _warps.Count;
    // DIAGNOSTIC/NAVIGATION VIEW ONLY - every map reachable through this build's STATIC WarpDefinition
    // graph (a warp's own source map, since the player is already standing there, or its
    // destination). This is deliberately NOT a hosting-scope/"which maps does this build serve"
    // signal: it only sees plain declarative warps, not scripted/OnTouch WarpTrigger transitions
    // (e.g. int_land*'s #intro_to_izlude_d, a runtime WarpAsync call - see
    // IntroToIzludeOnTouchScript), and has no concept of a character start_point/reconnect map with
    // no warp at all. MapServerWorld.Build's `servedMaps` parameter (hosting scope) is supplied
    // explicitly by the composition root (MapServerHostingScope.ServedMaps) and must never be
    // derived from this property.
    public IReadOnlySet<string> ReachableMaps => _warps.SelectMany(warp => new[] { warp.SourceMap, warp.DestinationMap }).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public int EntityCount => _entitiesById.Count;
    public int DynamicWarpActorCount => _dynamicWarpActorCount;
    public IReadOnlyDictionary<string, WorldEntityDefinition> EntitiesById => _entitiesById;
    public IEnumerable<WorldActor> GetVisibleWarpActors(string mapName, ushort x, ushort y, ushort range = WorldVisibilityOptions.DefaultAreaSize) => _worldActors.Where(actor => string.Equals(actor.MapName, mapName, StringComparison.OrdinalIgnoreCase) && Math.Abs((int)actor.X - x) <= range && Math.Abs((int)actor.Y - y) <= range);
    public bool TryGetActor(string entityIdOrName, string mapName, out WorldActor actor)
    {
        actor = _worldActors.FirstOrDefault(candidate => string.Equals(candidate.MapName, mapName, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(candidate.EntityId, entityIdOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate.Name, entityIdOrName, StringComparison.Ordinal) ||
             (candidate.EntityId is not null && _entitiesById.TryGetValue(candidate.EntityId, out var entity) && string.Equals(entity.Actor?.Name, entityIdOrName, StringComparison.Ordinal))))!;
        return actor is not null;
    }
    public bool TryGetActorName(uint actorId, string mapName, out string name)
    {
        var actor = _worldActors.FirstOrDefault(candidate => candidate.ActorId == actorId && string.Equals(candidate.MapName, mapName, StringComparison.OrdinalIgnoreCase));
        if (actor is null) { name = string.Empty; return false; }
        name = actor.EntityId is not null && _entitiesById.TryGetValue(actor.EntityId, out var entity) ? entity.Actor?.Name ?? actor.Name : actor.Name;
        return true;
    }
    public bool TryGetInteraction(uint actorId, string mapName, out WorldEntityDefinition entity, out ScriptBehaviorDefinition script)
    {
        if (_interactionsByActorId.TryGetValue(actorId, out var binding) && binding.Entity.Actor is not null && string.Equals(binding.Entity.Actor.Map, mapName, StringComparison.OrdinalIgnoreCase))
        {
            entity = binding.Entity; script = binding.Script; return true;
        }
        entity = null!; script = null!; return false;
    }
    public IEnumerable<NavigationDefinition> GetNavigationAt(string mapName, ushort x, ushort y) => _navigation.Where(item => item.Contains(mapName, x, y));
    public bool TryFindWarp(string mapName, ushort x, ushort y, out WarpDefinition warp) { warp = _warps.FirstOrDefault(candidate => candidate.Matches(mapName, x, y))!; return warp is not null; }
    public bool TryFindFirstWarpAlongRoute(string mapName, ushort fromX, ushort fromY, ushort toX, ushort toY, out WarpIntersection intersection)
    {
        foreach (var (x, y) in GridLineTraversal.Enumerate(fromX, fromY, toX, toY)) if (TryFindWarp(mapName, x, y, out var warp)) { intersection = new(warp, x, y); return true; }
        intersection = default; return false;
    }
    public bool TryFindFirstScriptTouchEnterAlongRoute(string mapName, ushort fromX, ushort fromY, ushort toX, ushort toY, out ScriptTouchIntersection intersection)
    {
        var candidates = _touchScripts.Where(binding => string.Equals(binding.Script.Map, mapName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var inside = candidates.ToDictionary(binding => binding.Entity.Id, binding => binding.Contains(fromX, fromY), StringComparer.OrdinalIgnoreCase);
        var first = true;
        foreach (var (x, y) in GridLineTraversal.Enumerate(fromX, fromY, toX, toY))
        {
            if (first) { first = false; continue; }
            foreach (var binding in candidates)
            {
                var now = binding.Contains(x, y);
                if (now && !inside[binding.Entity.Id]) { intersection = new(binding, x, y); return true; }
                inside[binding.Entity.Id] = now;
            }
        }
        intersection = default; return false;
    }

    private static WarpActorDefinition? ToActorDefinition(WorldEntityDefinition entity)
    {
        var actor = entity.Actor!;
        var trigger = entity.Triggers.FirstOrDefault();
        var script = entity.Scripts.FirstOrDefault();
        var radiusX = trigger?.RadiusX ?? script?.RadiusX ?? 0;
        var radiusY = trigger?.RadiusY ?? script?.RadiusY ?? 0;
        return radiusX <= byte.MaxValue && radiusY <= byte.MaxValue
            ? new(actor.Name, actor.Map, actor.X, actor.Y, (byte)radiusX, (byte)radiusY, actor.Class, actor.Direction, actor.EffectState, entity.Id)
            : null;
    }
    private static string SemanticKey(string map, string name) => $"{map}:{name}";
    // Academy's static-door warps plus the route-critical Izlude<->prt_fild08d and
    // prt_fild08d->prontera doors (ai/world-data.md's travel-corridor content); each area's
    // GeneratedWarps class is compiled independently (see tools/WorldDataImporter), so the
    // composed set is a plain concatenation rather than one area owning every WarpDefinition.
    private static IEnumerable<WarpDefinition> AllGeneratedWarps => GeneratedWarpRegistry.All;
    private static WorldMapRegistry LoadGenerated() => new(AllGeneratedWarps, GeneratedScriptRegistry.Entities, scripts: GeneratedScriptRegistry.Registry);

    // Same generated data as Tutorial/LoadGenerated(), but taking an externally supplied allocator
    // so MapServerWorld.Build() can hand WorldMapRegistry and MonsterRegistry the SAME
    // WorldActorIdAllocator instance instead of Tutorial's own private one.
    internal static WorldMapRegistry LoadGenerated(WorldActorIdAllocator allocator) =>
        new(AllGeneratedWarps, GeneratedScriptRegistry.Entities, scripts: GeneratedScriptRegistry.Registry, allocator: allocator);
}

public readonly record struct WarpIntersection(WarpDefinition Warp, ushort X, ushort Y);
public sealed record ScriptTouchBinding(WorldEntityDefinition Entity, WorldActor Actor, ScriptBehaviorDefinition Script)
{
    public bool Contains(ushort x, ushort y) => Math.Abs((int)x - Script.X) <= Script.RadiusX && Math.Abs((int)y - Script.Y) <= Script.RadiusY;
}
public readonly record struct ScriptTouchIntersection(ScriptTouchBinding Binding, ushort X, ushort Y);
public sealed record WarpActorDefinition(string Name, string MapName, ushort X, ushort Y, byte RadiusX, byte RadiusY, ushort SpriteClass = WorldActor.ClassId, byte Direction = 0, uint EffectState = 0, string? EntityId = null);
