namespace Athena.Net.MapServer.World;

// Owns every MobInstance for every generated MobSpawnDefinition across every
// map this process serves. Constructed explicitly once by MapServerWorld.Build()
// (composition root), not a hidden static singleton: unlike WorldMapRegistry.Tutorial
// (genuinely immutable world definition data), a MonsterRegistry holds LIVE MUTABLE
// runtime state (each MobInstance's current HP/alive-dead/respawn timers), so it is
// constructed once at startup and threaded down explicitly instead of being exposed
// as a lazily-initialized static property.
//
// The `allocator` parameter must be the SAME WorldActorIdAllocator instance passed to
// the composed WorldMapRegistry, so monster actor IDs share one namespace with NPC/
// warp actor IDs rather than each content kind getting its own arbitrary sub-range.
public sealed class MonsterRegistry
{
    private readonly TimeProvider _timeProvider;
    private readonly List<MobInstance> _instances = [];
    private readonly Dictionary<uint, MobInstance> _byActorId = [];

    public MonsterRegistry(IEnumerable<MobSpawnDefinition> spawns, WorldActorIdAllocator allocator, IMobSpawnCellSelector cellSelector, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        foreach (var spawn in spawns)
        {
            for (var i = 0; i < spawn.Count; i++)
            {
                var (x, y) = cellSelector.SelectCell(spawn, i);
                var instance = new MobInstance(allocator.Allocate(), spawn, x, y);
                _instances.Add(instance);
                _byActorId[instance.ActorId] = instance;
            }
        }
    }

    public IReadOnlyList<MobInstance> AllInstances => _instances;

    public IEnumerable<MobInstance> GetVisibleInstances(string mapName, ushort x, ushort y, ushort range = 14) =>
        _instances.Where(instance => instance.IsAlive
            && string.Equals(instance.Map, mapName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs((int)instance.X - x) <= range && Math.Abs((int)instance.Y - y) <= range);

    public bool TryGetInstance(uint actorId, string mapName, out MobInstance instance)
    {
        if (_byActorId.TryGetValue(actorId, out var candidate) && string.Equals(candidate.Map, mapName, StringComparison.OrdinalIgnoreCase))
        {
            instance = candidate;
            return true;
        }
        instance = null!;
        return false;
    }

    // Schedules a respawn for an instance that JUST transitioned to Dead
    // (idempotent: MobInstance.TryScheduleRespawn only succeeds once per
    // death). Respawn delay is the pinned spawn's RespawnDelayMs
    // (mob.delay1, npc_parse_mob).
    public void ScheduleRespawnIfNeeded(MobInstance instance)
    {
        var dueTicks = _timeProvider.GetUtcNow().AddMilliseconds(instance.Spawn.RespawnDelayMs).UtcTicks;
        instance.TryScheduleRespawn(dueTicks);
    }

    // Applies any respawns whose due time has passed. Callers (a background
    // loop, or a test driving TimeProvider directly) invoke this rather than
    // one Timer per monster instance, matching CharacterStatusEffectState's
    // "no timer per entry" scheduling philosophy.
    public int ProcessDueRespawns()
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var count = 0;
        foreach (var instance in _instances)
        {
            if (instance.TryRespawn(now)) count++;
        }
        return count;
    }
}
