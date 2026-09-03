using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;

namespace Athena.Net.World;

// Composition-time override for WorldPartitionGrain's active/touched-map policy window (default 5
// minutes when not supplied/registered in DI) - exists purely so tests can inject a much shorter
// window and observe the real unload-on-expiry/rebuild-on-touch behavior deterministically within
// a bounded real-wall-clock wait, without the production default ever needing to change. Never
// resolved by anything other than WorldPartitionGrain's own constructor.
public sealed record WorldMonsterTouchedWindowOptions(TimeSpan Window);

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
    // Constructed once per Rebuild (not per-tick) - MonsterRuntime itself holds no state beyond its
    // injected dependencies (all per-instance idle-walk/movement timing lives on MobInstance, via
    // Registry), so one instance safely serves every tick until the next Rebuild/Unload.
    private MonsterRuntime? _runtime;

    // Touched-window active-map policy (see this project's own "Inactive-map semantics" design):
    // updated by the grain on every call that represents genuine activity for this map (spawn
    // load, feed poll, attack/engagement mutation) - the grain's own tick loop consults this to
    // decide whether this map's simulation is still within its touched window, or has expired and
    // must be unloaded outright (never merely paused - see this type's own Unload doc comment for
    // why "stop ticking, later resume the same absolute deadlines" is explicitly rejected).
    public DateTimeOffset LastTouchedUtc { get; private set; }
    public void Touch(DateTimeOffset now) => LastTouchedUtc = now;

    public WorldMonsterMapSimulation(string mapId, DateTimeOffset now)
    {
        MapId = mapId;
        SimulationEpoch = WorldSimulationEpoch.NewEpoch();
        LastTouchedUtc = now;
    }

    // Explicit loaded/unloaded state - NEVER inferred merely from `Registry is null`, because a
    // brand-new, never-yet-loaded simulation ALSO has a null Registry and must be distinguishable
    // from one that WAS loaded and has since expired (see PollMonsterFeedAsync's own doc comment
    // for why a consumer needs to tell these apart). `IsLoaded` transitions Unloaded -> Loaded
    // exactly once per Rebuild call, and Loaded -> Unloaded exactly once per Unload call - Unload
    // is explicitly idempotent (a no-op if already Unloaded) so MonsterTickAsync calling it every
    // 100ms after expiry can never repeatedly discard state or rotate SimulationEpoch on every
    // single tick (the exact bug this idempotency fixes - a genuinely fresh epoch must only ever
    // be minted on an ACTUAL Unloaded->Loaded or Loaded->Unloaded transition, never on a no-op
    // repeat of a transition that already happened).
    public bool IsLoaded { get; private set; }

    // Discards ALL simulation state for this map outright - Registry, engagement, feed history,
    // incarnation tracking - rather than merely pausing a timer. This is the chosen policy
    // (over suspend-and-rebase) specifically because it cannot produce a giant wall-clock-gap
    // catch-up tick: there is nothing left to feed a stale elapsed-time delta into. The NEXT touch
    // (a fresh LoadMonsterSpawnsAsync call) rebuilds from static spawn definitions under a brand
    // new SimulationEpoch and requires a full bootstrap from any consumer - see Rebuild's own doc
    // comment for why a fresh epoch is exactly what already makes an old consumer's cursor
    // correctly stale. MapId/LastTouchedUtc themselves are NOT reset - Unload is something that
    // happens TO an existing simulation instance, the map identity persists across it.
    public void Unload()
    {
        if (!IsLoaded) return; // Idempotent - see IsLoaded's own doc comment for why this guard exists.
        IsLoaded = false;
        Registry = null;
        _runtime = null;
        _movementPathProvider = null;
        _collisionProvider = null;
        _spawnFingerprint = null;
        SimulationEpoch = WorldSimulationEpoch.NewEpoch();
        _engagementByActorId.Clear();
        _incarnationByActorId.Clear();
        _entries.Clear();
        _nextSequence = 1;
    }

    // A monster's engagement target needs BOTH CharacterId and PresenceId (see
    // WorldPlayerTargetReference's own doc comment for why CharacterId alone is not enough) -
    // MobInstance's own TryAcquireTarget/TryUnlockTarget only understand a single uint key (it has
    // no concept of a Guid presence at all), so the PresenceId half of that identity is tracked
    // HERE, at the grain-simulation layer, alongside (never instead of) MobInstance's own
    // uint-keyed engagement state - MobInstance.TryAcquireTarget is still called with CharacterId
    // as that uint key, so the two layers never disagree about WHICH character is targeted, only
    // this layer additionally remembers WHICH presence of that character it was.
    //
    // `State` is stored EXPLICITLY, never inferred from MobInstance.IsWalking - a monster can have
    // a target, be currently stationary, and still be out of range (chase path not started yet,
    // pathfinding not yet progressed) - IsWalking alone cannot distinguish that from genuinely
    // being in range. Whoever changes Target also computes and stores the correct State via
    // WorldMonsterEngagementRules at that exact moment (see TryAcquireEngagement) - Step 3 owns
    // continuously refreshing this State as the authoritative tick re-evaluates range/chase.
    private sealed class EngagementState
    {
        public WorldPlayerTargetReference? Target;
        public WorldMonsterEngagementState State = WorldMonsterEngagementState.Unengaged;
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
    //
    // `collisionProvider` drives the REAL, source-backed RathenaCompatibleMobSpawnCellSelector -
    // the same pinned spawn-cell search MapServer's own production runtime uses (see
    // MobSpawnCellSelector.cs's own doc comment for the full trace). UnverifiedFallbackMobSpawnCellSelector
    // (no walkability/map-bounds checking at all) is deliberately NEVER used here: this is
    // production World authority, not a collision-less test/dev composition - a test that wants
    // deterministic behavior supplies a real (even if minimal/synthetic) IMapCollisionProvider
    // instead of asking this type to silently degrade its own selection quality. A map with no
    // collision data loaded is a hard configuration error - RathenaCompatibleMobSpawnCellSelector
    // itself already throws InvalidOperationException for that (never a silent fallback), which is
    // exactly the behavior this method wants to preserve unmodified.
    public void Rebuild(IReadOnlyList<WorldMonsterSpawnDefinition> spawns, string fingerprint, Func<uint> allocateActorId, TimeProvider timeProvider, IMapCollisionProvider collisionProvider, IMovementPathProvider movementPathProvider)
    {
        var mobSpawnDefinitions = spawns.SelectMany(ExpandToMobSpawnDefinitions).ToArray();
        Registry = new MonsterRegistry(mobSpawnDefinitions, allocateActorId, new RathenaCompatibleMobSpawnCellSelector(collisionProvider), timeProvider);
        _runtime = new MonsterRuntime(Registry, collisionProvider, movementPathProvider, timeProvider);
        _movementPathProvider = movementPathProvider;
        _collisionProvider = collisionProvider;
        _spawnFingerprint = fingerprint;
        SimulationEpoch = WorldSimulationEpoch.NewEpoch();
        IsLoaded = true;
        _engagementByActorId.Clear();
        _incarnationByActorId.Clear();
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
        // The EXPLICITLY stored State, never re-inferred from IsWalking - see EngagementState's
        // own doc comment for why that inference is invalid (a target-holding, stationary,
        // not-yet-in-range mob is a real, reachable state this must not misreport as either
        // Chasing or InAttackRange).
        var engagementWireState = !instance.IsAlive || engagement?.Target is null ? WorldMonsterEngagementState.Unengaged : engagement.State;
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

    // `targetPresence`/`targetIsWalking` let this compute the CORRECT initial engagement state
    // (Unlock/Chase/InAttackRange) at the exact moment of acquisition via WorldMonsterEngagementRules
    // - never assumed, never left at a stale/default value. Step 3's own tick is what continuously
    // refreshes this afterward as the mob actually moves; this is only the one-time snapshot valid
    // at acquisition.
    //
    // If the authoritative evaluation itself says Unlock, acquisition must be REJECTED outright -
    // never store an EngagedTarget (or return Acquired/AlreadyCurrentTarget) for a target
    // WorldMonsterEngagementRules would immediately drop on the very next tick. The grain's own
    // NotifyMonsterAttackedAsync already screens out the common cause (dead/wrong-map attacker
    // presence) before calling this, but this check stays here too as the actual authoritative
    // gate - any other caller, or a presence that changes map/dies between the grain's screen and
    // this call, is still caught here.
    public WorldMonsterAttackedStatus TryAcquireEngagement(MobInstance instance, WorldPlayerTargetReference attacker, WorldPlayerPresence targetPresence, bool targetIsWalking)
    {
        if (!instance.IsAlive) return WorldMonsterAttackedStatus.MonsterNotAttackable;
        var mode = instance.Spawn.Mob.Mode;
        if (!mode.HasFlag(MobMode.CanAttack)) return WorldMonsterAttackedStatus.MonsterNotAttackable;
        var decision = WorldMonsterEngagementRules.Evaluate(instance, targetPresence, targetIsWalking);
        if (decision is WorldMonsterEngagementDecision.Unlock) return WorldMonsterAttackedStatus.AttackerNotEngageable;

        var state = _engagementByActorId.TryGetValue(instance.ActorId, out var existing) ? existing : _engagementByActorId[instance.ActorId] = new EngagementState();
        var alreadyCurrent = state.Target is { } current && current.CharacterId == attacker.CharacterId && current.PresenceId == attacker.PresenceId;
        if (!instance.TryAcquireTarget(attacker.CharacterId, mode)) return WorldMonsterAttackedStatus.MonsterNotAttackable;
        var wasUnengaged = state.Target is null;
        state.Target = attacker;
        state.State = decision switch
        {
            WorldMonsterEngagementDecision.InAttackRange => WorldMonsterEngagementState.InAttackRange,
            WorldMonsterEngagementDecision.Chase => WorldMonsterEngagementState.Chasing,
            _ => WorldMonsterEngagementState.Unengaged,
        };
        if (!alreadyCurrent)
        {
            // An EngagementAcquired entry may correctly report Chasing or InAttackRange depending
            // on the authoritative range decision just computed above - it must never claim
            // InAttackRange merely because the mob has not started walking yet (the exact
            // regression this method's own signature change fixes).
            Append(wasUnengaged ? WorldMonsterFeedEntryKind.EngagementAcquired : WorldMonsterFeedEntryKind.ChaseStarted, instance);
        }
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

    private static long RandomJitterMs() => System.Random.Shared.Next(0, 1000);

    // ONE World-owned tick for this map, in this exact order:
    //   1. due respawns (bumps IncarnationId, appends Respawned)
    //   2. for every CURRENTLY ENGAGED mob: consume any pending combat retarget EXACTLY at the cell
    //      boundary it is actually reached, via MobInstance.AdvanceMovementForCombat - never plain
    //      AdvanceMovement, which does NOT consume PendingChaseDestination at all (see that
    //      method's own contract). This must run BEFORE this tick's own engagement re-evaluation,
    //      matching MonsterEngagementTickProcessor's own established MapServer-side ordering
    //      exactly (see that type's own ProcessAsync for the identical sequencing this mirrors).
    //   3. MonsterRuntime.ProcessTick() for every mob (idle-walk + the ordinary AdvanceMovement
    //      fallback) - safe to call unconditionally AFTER step 2 because ProcessIdleMovement's own
    //      HasActiveTarget guard already prevents idle-walk from starting on an engaged mob, and an
    //      engaged mob already advanced to `now` in step 2 makes this call's own AdvanceMovement a
    //      genuine no-op for it (CharacterMovementState.AdvanceTo crosses zero further cells for a
    //      `now` it has already reached) - never a double-advance, never a double-reported change.
    //   4. the source-backed target-validity/range re-evaluation (WorldMonsterEngagementRules, NOT
    //      the full original MonsterEngagementDomain.Evaluate - cadence/Attack/Wait stay
    //      MapServer-local) for every currently engaged mob, deciding Unlock/continued-chase/
    //      stop-chase-into-range.
    // `resolvePresence`/`isWalking` are grain-owned lookups (player presence registration and
    // _movements membership) passed in as delegates so this type never needs its own reference to
    // the grain's player-tracking state.
    public void Tick(DateTimeOffset now, Func<uint, WorldPlayerPresence?> resolvePresence, Func<uint, bool> isWalking)
    {
        if (Registry is null || _runtime is null) return;

        foreach (var respawned in Registry.ProcessDueRespawns())
        {
            OnRespawnObserved(respawned);
        }

        // Step 2: engaged-mob combat-retarget consumption, BEFORE step 3's plain ProcessTick and
        // BEFORE step 4's own fresh engagement decision - see this method's own doc comment for why
        // this exact ordering is required (AdvanceMovementForCombat is the ONLY method that ever
        // consumes MobInstance.PendingChaseDestination at a real cell boundary).
        foreach (var actorId in _engagementByActorId.Keys.ToArray())
        {
            if (!TryFind(actorId, out var instance) || !instance.IsAlive) continue;
            var (crossed, retargetApplied) = instance.AdvanceMovementForCombat(
                now,
                (fromX, fromY, toX, toY) => _collisionProvider is not null && _collisionProvider.TryGetMap(instance.Map, out _) && _movementPathProvider is not null
                    ? _movementPathProvider.ComputePath(instance.Map, fromX, fromY, toX, toY)
                    : [],
                instance.Spawn.Mob.WalkSpeed);
            if (retargetApplied)
            {
                // The replacement path's own first leg is exactly the "movement changed, tell
                // observers" event a fresh chase-start already produces - mirrors
                // MonsterEngagementTickProcessor's own identical WalkStarted-shaped report for this
                // exact case (see that type's own ProcessAsync comment).
                Append(WorldMonsterFeedEntryKind.ChaseStarted, instance);
            }
            else if (crossed.Count > 0)
            {
                // An ordinary chase cell-crossing (or the walk finishing) - World is authoritative
                // for position, so this MUST update the feed even though it will almost always
                // require no Ragexe wire packet from a consumer for an already-visible actor (see
                // WorldMonsterFeedEntryKind.Moved's own doc comment for the CellCrossed/WalkFinished
                // "projection update, no fabricated packet" contract this satisfies for engaged
                // mobs too, not only idle ones).
                Append(WorldMonsterFeedEntryKind.Moved, instance);
            }
        }

        foreach (var change in _runtime.ProcessTick())
        {
            // Only reported here as ordinary Moved when the mob has NO current engagement - an
            // engaged mob's own movement was already reported by step 2 above (this call's own
            // AdvanceMovement branch is a safe no-op for it, per this method's own doc comment, so
            // it never reaches this loop body a second time for the same crossing).
            if (!_engagementByActorId.ContainsKey(change.Instance.ActorId))
                Append(WorldMonsterFeedEntryKind.Moved, change.Instance);
        }

        // A snapshot of keys, not a live enumeration - Unlock below mutates _engagementByActorId,
        // which would otherwise invalidate an in-progress Dictionary enumeration over it.
        foreach (var actorId in _engagementByActorId.Keys.ToArray())
        {
            if (!TryFind(actorId, out var instance) || !instance.IsAlive) continue;
            var state = _engagementByActorId[actorId];
            if (state.Target is not { } target) continue;

            var targetPresence = resolvePresence(target.CharacterId);
            // A resolved presence whose CURRENT PresenceId no longer matches this engagement's own
            // stored target reference is exactly a stale-reconnect situation (see
            // WorldPlayerTargetReference's own doc comment) - treated identically to "target gone"
            // for this authoritative re-evaluation, never silently re-attributed to the new presence.
            var validTargetPresence = targetPresence is { } presence && presence.PresenceId == target.PresenceId ? presence : null;
            var decision = WorldMonsterEngagementRules.Evaluate(instance, validTargetPresence, isWalking(target.CharacterId));

            switch (decision)
            {
                case WorldMonsterEngagementDecision.Unlock:
                    Unlock(instance, now, RandomJitterMs);
                    break;

                case WorldMonsterEngagementDecision.Chase chase:
                    var previousState = state.State;
                    state.State = WorldMonsterEngagementState.Chasing;
                    if (ApplyChaseDecision(instance, chase, now) || previousState != WorldMonsterEngagementState.Chasing)
                        Append(WorldMonsterFeedEntryKind.ChaseStarted, instance);
                    break;

                case WorldMonsterEngagementDecision.InAttackRange:
                    var wasChasing = instance.IsWalking;
                    var wasInAttackRangeAlready = state.State == WorldMonsterEngagementState.InAttackRange;
                    state.State = WorldMonsterEngagementState.InAttackRange;
                    if (wasChasing)
                    {
                        // A walking mob reaching attack range is a genuine chase interruption
                        // (pinned USW_FIXPOS) - MapServer's own local attack executor still owns
                        // cadence/Attack itself (this World-side tick never calls EnterAttackState/
                        // ScheduleNextAttack - that remains entirely MapServer-local), but the
                        // STOP-CHASE half of that transition is simulation-owned and must happen
                        // here so the mob's authoritative position/IsWalking freezes correctly.
                        instance.StopChase();
                        Append(WorldMonsterFeedEntryKind.ChaseInterrupted, instance);
                    }
                    else if (!wasInAttackRangeAlready)
                    {
                        Append(WorldMonsterFeedEntryKind.InAttackRange, instance);
                    }
                    break;
            }
        }
    }

    // Pinned mob_ai_sub_hard's own out-of-range branch (mob.cpp:2213's unit_walktobl) - mirrors
    // MonsterEngagementTickProcessor.ApplyChaseDecision's own MapServer-side logic exactly (see
    // that method's own doc comment for the full pinned trace), narrowed to exclude anything
    // Attack/cadence-related, which stays MapServer-local. Returns true only when a FRESH walk was
    // started here - a mid-walk retarget is instead consumed by this method's OWN caller (Tick's
    // step 2, via AdvanceMovementForCombat), so this method must not double-report it.
    private bool ApplyChaseDecision(MobInstance instance, WorldMonsterEngagementDecision.Chase chase, DateTimeOffset now)
    {
        if (instance.IsWalking)
        {
            var alreadyTargeting = instance.PendingChaseDestination is { } pending
                ? pending.X == chase.DestinationX && pending.Y == chase.DestinationY
                : instance.MovementDestination.X == chase.DestinationX && instance.MovementDestination.Y == chase.DestinationY;
            if (alreadyTargeting) return false;
        }

        if (instance.TryRetargetChase(chase.DestinationX, chase.DestinationY))
        {
            instance.EnterChaseState();
            return false; // Deferred to the next cell boundary - Tick's own step 2 (AdvanceMovementForCombat) reports it when applied.
        }

        if (Registry is null || _movementPathProvider is null || _collisionProvider is null) return false;
        if (!_collisionProvider.TryGetMap(instance.Map, out _)) return false;
        var position = instance.GetPosition();
        var path = _movementPathProvider.ComputePath(instance.Map, position.X, position.Y, chase.DestinationX, chase.DestinationY);
        if (path.Count < 2) return false;
        if (!instance.TryStartChase(path, instance.Spawn.Mob.WalkSpeed, now)) return false;
        instance.EnterChaseState();
        return true;
    }

    // Stored at Rebuild time alongside _runtime - ApplyChaseDecision needs direct path computation
    // (not merely what MonsterRuntime's own idle-walk scheduling already does internally), matching
    // MonsterEngagementTickProcessor's own identical need in MapServer.
    private IMovementPathProvider? _movementPathProvider;
    private IMapCollisionProvider? _collisionProvider;

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
        // Never-loaded or unloaded-and-not-since-touched: the caller MUST call
        // LoadMonsterSpawnsAsync before treating anything here as authoritative - Snapshot is an
        // EMPTY placeholder, never a real (possibly legitimately zero-monster) snapshot. See
        // WorldMonsterFeedStatus's own doc comment for why this must never be confused with
        // WorldMonsterFeedStatus.Ready against a genuinely loaded, zero-monster map.
        if (!IsLoaded)
            return new WorldMonsterFeedPage(MapId, SimulationEpoch, WorldMonsterFeedStatus.SpawnInitializationRequired, Snapshot: [], Entries: null, AsOfSequence: 0);

        var snapshot = Registry!.AllInstances.Select(ToWireInstance).ToArray();
        if (cursor is null)
            return new WorldMonsterFeedPage(MapId, SimulationEpoch, WorldMonsterFeedStatus.Ready, snapshot, Entries: null, AsOfSequence);
        if (!cursor.Value.SimulationEpoch.Equals(SimulationEpoch))
            return new WorldMonsterFeedPage(MapId, SimulationEpoch, WorldMonsterFeedStatus.ResyncRequired, snapshot, Entries: null, AsOfSequence);
        var oldestRetained = _entries.Count > 0 ? _entries[0].Sequence : _nextSequence;
        if (cursor.Value.Sequence < oldestRetained - 1 || cursor.Value.Sequence > AsOfSequence)
            return new WorldMonsterFeedPage(MapId, SimulationEpoch, WorldMonsterFeedStatus.ResyncRequired, snapshot, Entries: null, AsOfSequence);
        var incremental = _entries.Where(entry => entry.Sequence > cursor.Value.Sequence).ToArray();
        return new WorldMonsterFeedPage(MapId, SimulationEpoch, WorldMonsterFeedStatus.Ready, Snapshot: null, incremental, AsOfSequence);
    }
}
