namespace Athena.Net.MapServer.World;

// Owns every MobInstance for every generated MobSpawnDefinition across every
// map this process serves. Constructed explicitly once by MapServerWorld.Build()
// (composition root), not a hidden static singleton: unlike WorldMapRegistry.Tutorial
// (genuinely immutable world definition data), a MonsterRegistry holds LIVE MUTABLE
// runtime state (each MobInstance's current HP/alive-dead/respawn timers), so it is
// constructed once at startup and threaded down explicitly instead of being exposed
// as a lazily-initialized static property.
//
// `allocateActorId` must draw from the SAME actor-ID namespace as whatever else shares this
// composed world's identity space (NPC/warp actors in MapServer's own WorldActorIdAllocator; the
// grain's own per-partition PartitionWorldActorIdAllocator in Athena.World.Monsters). Deliberately
// a bare `Func<uint>` delegate rather than a concrete allocator type or a new single-method
// interface: this is the exact pattern IMobSpawnCellSelector.TrySelectCell and
// MonsterRegistry.ProcessDueRespawns' own `selectPosition` callback already use elsewhere in this
// class, and it is what lets this same MonsterRegistry class - file-linked unchanged into
// Athena.World.Monsters (see that project's own README/csproj) - be constructed with either
// WorldActorIdAllocator.Allocate (MapServer's own composition) or
// PartitionWorldActorIdAllocator.Allocate (the grain's composition) without MonsterRegistry itself
// referencing either concrete type or Athena.World.Contracts at all.
public sealed class MonsterRegistry
{
    private readonly TimeProvider _timeProvider;
    private readonly IMobSpawnCellSelector _cellSelector;
    private readonly List<MobInstance> _instances = [];
    private readonly Dictionary<uint, MobInstance> _byActorId = [];

    public MonsterRegistry(IEnumerable<MobSpawnDefinition> spawns, Func<uint> allocateActorId, IMobSpawnCellSelector cellSelector, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _cellSelector = cellSelector;
        foreach (var spawn in spawns)
        {
            for (var i = 0; i < spawn.Count; i++)
            {
                // Pinned mob_spawn does NOT treat exhausting its 8+50 random-attempt budget as a
                // fatal configuration error - it schedules mob_delayspawn and retries later
                // (mob.cpp:1152-1159). Athena matches that exactly at startup too: an instance that
                // cannot be placed immediately is created "pending" (Dead, respawn already due) via
                // MobInstance.CreatePending, reusing the EXISTING ProcessDueRespawns retry sweep -
                // never a thrown exception, and never a silent fallback coordinate.
                var instance = cellSelector.TrySelectCell(spawn, i, out var position)
                    ? new MobInstance(allocateActorId(), spawn, position.X, position.Y)
                    : MobInstance.CreatePending(allocateActorId(), spawn, timeProvider.GetUtcNow().UtcTicks);
                _instances.Add(instance);
                _byActorId[instance.ActorId] = instance;
            }
        }
    }

    public IReadOnlyList<MobInstance> AllInstances => _instances;

    public IEnumerable<MobInstance> GetVisibleInstances(string mapName, ushort x, ushort y, ushort range = WorldVisibilityOptions.DefaultAreaSize) =>
        _instances.Where(instance =>
        {
            if (!instance.IsAlive || !string.Equals(instance.Map, mapName, StringComparison.OrdinalIgnoreCase)) return false;
            var position = instance.GetPosition(); // One atomic snapshot - never torn between axes.
            return Math.Abs((int)position.X - x) <= range && Math.Abs((int)position.Y - y) <= range;
        });

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
    // death). Respawn delay is the pinned spawn's RespawnDelay (mob.delay1,
    // npc_parse_mob). RespawnRandomDelay (mob.delay2) is preserved losslessly
    // on MobSpawnDefinition but deliberately NOT consumed here yet - pinned
    // mob_delay_amount (mob.cpp:1071-1073) adds `rnd()%delay2` on top of
    // delay1, which remains an explicit, documented runtime gap (not a data
    // gap) rather than new scheduler behavior introduced by this branch.
    public void ScheduleRespawnIfNeeded(MobInstance instance)
    {
        var dueTicks = _timeProvider.GetUtcNow().AddMilliseconds(instance.Spawn.RespawnDelay).UtcTicks;
        instance.TryScheduleRespawn(dueTicks);
    }

    // Applies any respawns whose due time has passed. Callers (a background
    // loop, or a test driving TimeProvider directly) invoke this rather than
    // one Timer per monster instance, matching CharacterStatusEffectState's
    // "no timer per entry" scheduling philosophy. Returns the instances that ACTUALLY respawned
    // this call (not merely a count) - MapTcpServer's own live tick loop needs to know WHICH
    // instances came back so it can fan out a fresh client-facing spawn/stand notification to any
    // session whose visibility now covers the respawn position; a killer session had already
    // removed the actor from its own _visibleActorIds on death (see
    // MapClientSession's existing vanish-on-death handling), so nothing else re-discovers a
    // respawned instance on its own once respawned.
    public IReadOnlyList<MobInstance> ProcessDueRespawns()
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var respawned = new List<MobInstance>();
        foreach (var instance in _instances)
        {
            // instanceIndex=0: TrySelectCell's instanceIndex parameter only has meaning for the
            // initial batch-spawn loop above (spreading UnverifiedFallbackMobSpawnCellSelector's
            // deterministic placeholder row across N instances); a respawn is a single independent
            // re-selection for one already-existing instance, matching pinned mob_spawn re-running
            // map_search_freecell fresh on every call with no memory of "which instance number"
            // this is.
            //
            // A `false` TrySelectCell result (attempt budget exhausted - a genuine temporary
            // failure, see IMobSpawnCellSelector's own doc comment) makes TryRespawn itself return
            // false and leave the instance Dead with its respawn already scheduled - the NEXT call
            // to this same ProcessDueRespawns sweep will try again, exactly matching pinned
            // mob_spawn's own mob_delayspawn retry-later behavior without introducing any new
            // timer/scheduler.
            if (instance.TryRespawn(now, () => _cellSelector.TrySelectCell(instance.Spawn, 0, out var position) ? (true, position) : (false, default)))
                respawned.Add(instance);
        }
        return respawned;
    }
}
