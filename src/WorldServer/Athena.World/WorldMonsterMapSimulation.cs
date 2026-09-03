using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.World;

// One map's worth of Phase 2B monster SIMULATION state, owned entirely by WorldPartitionGrain -
// never its own grain (see IWorldPartitionGrain.cs's own doc comment for the approved
// architecture: one coarse WorldPartitionGrain, no MonsterGrain/MapGrain/CellGrain). Reuses the
// SAME MobInstance/MonsterRegistry/IMobSpawnCellSelector types MapServer's own MonsterRuntime
// already drives, file-linked into Athena.World.Monsters - this is deliberately the "World's own
// copy" of monster simulation state, not a duplicate implementation of pinned spawn/movement
// logic (see MobSpawnCellSelector.cs's own doc comment for that logic's pinned trace).
//
// Deliberately does NOT track CurrentHp for combat purposes - MobInstance's own CurrentHp field
// exists (MonsterRegistry constructs real MobInstance objects, which always have one), but nothing
// in this type ever mutates it via ApplyDamage, and WorldMonsterInstance (the wire-facing
// projection) never exposes it - see that record's own doc comment for why player -> monster
// damage stays entirely MapServer-local for this phase.
internal sealed class WorldMonsterMapSimulation
{
    private readonly Dictionary<uint, EngagementState> _engagementByActorId = [];
    private readonly List<WorldMonsterFeedEntry> _entries = [];
    private long _nextSequence = 1;
    private string? _spawnFingerprint;

    public string MapId { get; }
    public WorldSimulationEpoch SimulationEpoch { get; private set; }
    public MonsterRegistry? Registry { get; private set; }

    public WorldMonsterMapSimulation(string mapId)
    {
        MapId = mapId;
        SimulationEpoch = WorldSimulationEpoch.NewEpoch();
    }

    // A monster's engagement target needs BOTH CharacterId and PresenceId (see
    // WorldPlayerTargetReference's own doc comment for why CharacterId alone is not enough) -
    // MobInstance's own TryAcquireTarget/TryUnlockTarget only understand a single uint key (it has
    // no concept of a Guid presence at all), so the PresenceId half of that identity is tracked
    // HERE, at the grain-simulation layer, alongside (never instead of) MobInstance's own
    // uint-keyed engagement state - MobInstance.TryAcquireTarget is still called with CharacterId
    // as that uint key, so the two layers never disagree about WHICH character is targeted, only
    // this layer additionally remembers WHICH presence of that character it was.
    private sealed class EngagementState
    {
        public WorldPlayerTargetReference? Target;
    }

    // Deterministic, order-independent canonical fingerprint over a batch's actual spawn content -
    // never trusted from the caller (see WorldMonsterSpawnBatch's own doc comment for why). Two
    // batches presented with their Spawns list in a different order still hash identically because
    // every spawn's own canonical string is sorted before combining.
    public static string ComputeContentFingerprint(IReadOnlyList<WorldMonsterSpawnDefinition> spawns)
    {
        var canonicalRows = spawns
            .Select(spawn => string.Join('|', spawn.MobId, spawn.MapId.ToLowerInvariant(), spawn.X, spawn.Y, spawn.Xs, spawn.Ys,
                spawn.Count, spawn.RespawnDelayMs, spawn.RespawnRandomDelayMs, spawn.SpawnName, spawn.WalkSpeedMs, spawn.AttackRange,
                spawn.MaxHp, spawn.Mode))
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();
        var combined = string.Join('\n', canonicalRows);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexStringLower(hash);
    }

    // Returns null (never throws) when any spawn in the batch does not belong to this simulation's
    // own MapId - the caller (WorldPartitionGrain.LoadMonsterSpawnsAsync) turns that into the
    // explicit SpawnMapMismatch status; this type has no wire-facing status enum of its own to
    // return one from directly.
    public bool AllSpawnsBelongToThisMap(IReadOnlyList<WorldMonsterSpawnDefinition> spawns) =>
        spawns.All(spawn => string.Equals(spawn.MapId, MapId, StringComparison.OrdinalIgnoreCase));

    public string? CurrentFingerprint => _spawnFingerprint;

    // Builds fresh MonsterRegistry state from the given spawns under a NEW SimulationEpoch - used
    // both for the very first load and for the inactive-map unload/rebuild policy (a fresh touch
    // after expiry always gets a new epoch, never reuses the old one, per that policy's own
    // "fresh-epoch-rebuild-on-touch" design). allocateActorId is synchronous (MonsterRegistry's own
    // constructor requires it) - the caller pre-leases exactly spawns.Sum(s => s.Count) actor IDs
    // via LeasedBlockActorIdAllocator BEFORE calling this, since leasing itself is async and cannot
    // happen lazily inside MonsterRegistry's synchronous construction.
    public void Rebuild(IReadOnlyList<WorldMonsterSpawnDefinition> spawns, string fingerprint, Func<uint> allocateActorId, TimeProvider timeProvider)
    {
        var mobSpawnDefinitions = spawns.SelectMany(ExpandToMobSpawnDefinitions).ToArray();
        Registry = new MonsterRegistry(mobSpawnDefinitions, allocateActorId, new UnverifiedFallbackMobSpawnCellSelector(), timeProvider);
        _spawnFingerprint = fingerprint;
        SimulationEpoch = WorldSimulationEpoch.NewEpoch();
        _engagementByActorId.Clear();
        _entries.Clear();
        _nextSequence = 1;
    }

    // One WorldMonsterSpawnDefinition -> one MobSpawnDefinition PER instance is NOT how
    // MonsterRegistry works - MonsterRegistry itself expands Count internally (see its own
    // constructor's `for (var i = 0; i < spawn.Count; i++)` loop), so this only needs to build ONE
    // MobSpawnDefinition per declaration, carrying Count through unchanged.
    private static IEnumerable<MobSpawnDefinition> ExpandToMobSpawnDefinitions(WorldMonsterSpawnDefinition spawn)
    {
        var mob = new MobDefinition(
            spawn.MobId, AegisName: $"MOB_{spawn.MobId}", Name: spawn.SpawnName, Level: 1, MaxHp: spawn.MaxHp,
            Attack: 0, Attack2: 0, Defense: 0, MagicDefense: 0,
            Str: 0, Agi: 0, Vit: 0, Int: 0, Dex: 0, Luk: 0,
            AttackRange: spawn.AttackRange, WalkSpeed: spawn.WalkSpeedMs, AttackDelay: 0, AttackMotion: 0, DamageMotion: 0,
            BaseExp: 0, JobExp: 0, Mode: (MobMode)spawn.Mode,
            Source: new WorldSourceInfo("world-partition-grain", "n/a", "wire-projection", 0));
        yield return new MobSpawnDefinition(
            mob, spawn.MapId, spawn.Count, spawn.RespawnDelayMs, spawn.RespawnRandomDelayMs,
            mob.Source, spawn.SpawnName, (short)spawn.X, (short)spawn.Y, (short)spawn.Xs, (short)spawn.Ys);
    }

    public int PendingActorIdCount(IReadOnlyList<WorldMonsterSpawnDefinition> spawns) => spawns.Sum(spawn => spawn.Count);

    public WorldMonsterInstance ToWireInstance(MobInstance instance)
    {
        var position = instance.GetPosition();
        var engagement = _engagementByActorId.TryGetValue(instance.ActorId, out var state) ? state : null;
        var engagementWireState = !instance.IsAlive || engagement?.Target is null
            ? WorldMonsterEngagementState.Unengaged
            : instance.IsWalking ? WorldMonsterEngagementState.Chasing : WorldMonsterEngagementState.InAttackRange;
        return new WorldMonsterInstance(
            instance.ActorId,
            new WorldMonsterIncarnationId(IncarnationOf(instance)),
            MapId,
            instance.Spawn.Mob.Id,
            position.X, position.Y,
            instance.IsAlive ? WorldMonsterLifecycleState.Alive : WorldMonsterLifecycleState.Dead,
            instance.IsWalking,
            instance.MovementDestination.X, instance.MovementDestination.Y,
            engagementWireState,
            engagement?.Target);
    }

    // Placeholder incarnation derivation until MobInstance itself is extended with a real
    // IncarnationId counter (a MapServer-shared file-linked change, out of THIS step's own scope -
    // Step 2 is grain state/mutation/feed correctness, not a MobInstance signature change every
    // existing MapServer caller would also need to absorb). Tracked separately here per-ActorId so
    // respawn-driven increments (see MarkDead/OnRespawnObserved) are still meaningful for THIS
    // grain's own stale-incarnation rejection tests even before that shared change lands.
    private readonly Dictionary<uint, long> _incarnationByActorId = [];
    private long IncarnationOf(MobInstance instance) =>
        _incarnationByActorId.TryGetValue(instance.ActorId, out var value) ? value : (_incarnationByActorId[instance.ActorId] = WorldMonsterIncarnationId.First.Value);

    public bool TryFind(uint actorId, out MobInstance instance)
    {
        instance = null!;
        if (Registry is null) return false;
        var found = Registry.AllInstances.FirstOrDefault(candidate => candidate.ActorId == actorId);
        if (found is null) return false;
        instance = found;
        return true;
    }

    public bool MatchesLife(MobInstance instance, WorldMonsterLifeReference reference) =>
        string.Equals(MapId, reference.MapId, StringComparison.OrdinalIgnoreCase) &&
        SimulationEpoch.Equals(reference.SimulationEpoch) &&
        instance.ActorId == reference.ActorId &&
        IncarnationOf(instance) == reference.IncarnationId.Value;

    public WorldMonsterDeathStatus MarkDead(MobInstance instance)
    {
        if (!instance.IsAlive || Registry is null) return WorldMonsterDeathStatus.AlreadyDead;
        // World does not own HP/damage (see this type's own doc comment) - ApplyDamage is reused
        // here purely for its Alive->Dead transition (and its own existing target-unlock-on-death
        // side effect, mob.cpp:3863), passing the instance's CURRENT Hp as the damage amount so the
        // call is unconditionally lethal regardless of whatever HP MapServer's own local combat
        // state most recently had, without this type ever needing to know or care what that value
        // was. The (HpBefore, HpAfter, killed) return is intentionally discarded - a wire-facing
        // WorldMonsterInstance never carries HP at all.
        instance.ApplyDamage(instance.CurrentHp);
        // World owns respawn TIMING (per the approved scope boundary) - reuse MonsterRegistry's
        // own existing, pinned-delay-backed scheduling rather than re-deriving it here.
        Registry.ScheduleRespawnIfNeeded(instance);
        _engagementByActorId.Remove(instance.ActorId);
        Append(WorldMonsterFeedEntryKind.Died, instance);
        return WorldMonsterDeathStatus.MarkedDead;
    }

    // Called once per tick (Step 3, not yet wired here) after MonsterRegistry.ProcessDueRespawns
    // reports a respawned instance - bumps the tracked incarnation and appends the feed entry a
    // consumer needs to reset its own local combat-target state (see the plan's own "Respawn for a
    // new IncarnationId resets local combat state to full HP" rule).
    public void OnRespawnObserved(MobInstance instance)
    {
        _incarnationByActorId[instance.ActorId] = IncarnationOf(instance) + 1;
        Append(WorldMonsterFeedEntryKind.Respawned, instance);
    }

    public WorldMonsterAttackedStatus TryAcquireEngagement(MobInstance instance, WorldPlayerTargetReference attacker)
    {
        if (!instance.IsAlive) return WorldMonsterAttackedStatus.MonsterNotAttackable;
        var mode = instance.Spawn.Mob.Mode;
        if (!mode.HasFlag(MobMode.CanAttack)) return WorldMonsterAttackedStatus.MonsterNotAttackable;
        var state = _engagementByActorId.TryGetValue(instance.ActorId, out var existing) ? existing : _engagementByActorId[instance.ActorId] = new EngagementState();
        var alreadyCurrent = state.Target is { } current && current.CharacterId == attacker.CharacterId && current.PresenceId == attacker.PresenceId;
        if (!instance.TryAcquireTarget(attacker.CharacterId, mode)) return WorldMonsterAttackedStatus.MonsterNotAttackable;
        var wasUnengaged = state.Target is null;
        state.Target = attacker;
        if (!alreadyCurrent) Append(wasUnengaged ? WorldMonsterFeedEntryKind.EngagementAcquired : WorldMonsterFeedEntryKind.ChaseStarted, instance);
        return alreadyCurrent ? WorldMonsterAttackedStatus.AlreadyCurrentTarget : WorldMonsterAttackedStatus.Acquired;
    }

    public WorldPlayerTargetReference? CurrentTarget(uint actorId) => _engagementByActorId.TryGetValue(actorId, out var state) ? state.Target : null;

    public void Unlock(MobInstance instance, DateTimeOffset now, Func<long> jitterMs)
    {
        if (_engagementByActorId.Remove(instance.ActorId))
        {
            instance.TryUnlockTarget(now, jitterMs);
            Append(WorldMonsterFeedEntryKind.TargetUnlocked, instance);
        }
    }

    private void Append(WorldMonsterFeedEntryKind kind, MobInstance instance)
    {
        var entry = new WorldMonsterFeedEntry(_nextSequence++, kind, instance.ActorId, new WorldMonsterIncarnationId(IncarnationOf(instance)), ToWireInstance(instance));
        _entries.Add(entry);
        // Bounded: this is a development-slice retention window, not an unbounded log - a consumer
        // that falls further behind than this receives ResyncRequired (see BuildPage), never a
        // silently-truncated gap it can't detect. 4096 is generously larger than any plausible
        // single-poll-interval burst of engagement/lifecycle transitions for one map.
        const int retention = 4096;
        if (_entries.Count > retention) _entries.RemoveRange(0, _entries.Count - retention);
    }

    public long AsOfSequence => _nextSequence - 1;

    public WorldMonsterFeedPage BuildPage(WorldMonsterFeedCursor? cursor)
    {
        var snapshot = Registry?.AllInstances.Select(ToWireInstance).ToArray() ?? [];
        if (cursor is null)
            return new WorldMonsterFeedPage(MapId, SimulationEpoch, ResyncRequired: false, snapshot, Entries: null, AsOfSequence);
        if (!cursor.Value.SimulationEpoch.Equals(SimulationEpoch))
            return new WorldMonsterFeedPage(MapId, SimulationEpoch, ResyncRequired: true, snapshot, Entries: null, AsOfSequence);
        var oldestRetained = _entries.Count > 0 ? _entries[0].Sequence : _nextSequence;
        if (cursor.Value.Sequence < oldestRetained - 1 || cursor.Value.Sequence > AsOfSequence)
            return new WorldMonsterFeedPage(MapId, SimulationEpoch, ResyncRequired: true, snapshot, Entries: null, AsOfSequence);
        var incremental = _entries.Where(entry => entry.Sequence > cursor.Value.Sequence).ToArray();
        return new WorldMonsterFeedPage(MapId, SimulationEpoch, ResyncRequired: false, Snapshot: null, incremental, AsOfSequence);
    }
}
