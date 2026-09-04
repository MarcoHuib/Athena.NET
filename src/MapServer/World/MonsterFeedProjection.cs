using System.Collections.Immutable;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Exactly ONE per-map consumer owns SimulationEpoch/WorldMonsterFeedCursor/the current monster
// projection snapshot/engagement projection - see IWorldPartitionGrain's own "single per-map
// MapServer feed consumer" contract (PollMonsterFeedAsync's own doc comment). MapClientSession
// must NEVER independently call PollMonsterFeedAsync or fetch a second bootstrap snapshot - every
// session on the same map observes the SAME MonsterFeedProjection instance (owned by
// MonsterFeedProjectionRegistry, one per currently-active map).
//
// Binding bootstrap/resync ordering (never reordered):
//   1. receive Snapshot + SimulationEpoch + AsOfSequence from PollMonsterFeedAsync
//   2. reconcile the shared monster projection (this type's own _byActorId dictionary)
//   3. reconcile local combat-state identities (MonsterCombatStateStore - old epoch discarded,
//      new incarnations get fresh full-HP entries, SAME life's entry is preserved untouched)
//   4. reconcile every active session's actual client-visible monster projection (via the
//      caller-supplied `reconcileSessions` callback - MapTcpServer owns session enumeration, this
//      type does not)
//   5. ONLY THEN advance `Cursor` to (SimulationEpoch, AsOfSequence)
// This ordering exists specifically so a crash/exception between steps 2-4 never leaves the cursor
// advanced past state a session did not actually get to observe.
//
// Thread-safety: this type is read AND written from several independently-scheduled call sites -
// MapTcpServer's own monster-feed tick loop (ApplySnapshot/ApplyEntry/CommitCursor), MapClientSession's
// own packet-handling loop and its own repeat-attack loop (TryGetLife/TryGetInstance/AllInstances/
// EngagementOf), and MonsterAttackCadenceExecutor/MonsterSpatialInspector (read-only consumers). All
// mutable state (_byActorId, _engagementByActorId, Cursor, CurrentEpoch) is guarded by ONE `Lock`,
// mirroring MonsterCombatStateStore's own exact `private readonly Lock _gate = new(); lock (_gate)
// { ... }` convention - every operation here is O(map monster count) in-memory work with no I/O, so
// holding the lock for the whole operation is cheap and never blocks on a network write.
public sealed class MonsterFeedProjection(string mapId)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, WorldMonsterInstance> _byActorId = [];
    private readonly Dictionary<uint, WorldMonsterEngagementState> _engagementByActorId = [];
    private WorldMonsterFeedCursor? _cursor;
    private WorldSimulationEpoch? _currentEpoch;

    public string MapId { get; } = mapId;
    public WorldMonsterFeedCursor? Cursor { get { lock (_gate) return _cursor; } }
    public WorldSimulationEpoch? CurrentEpoch { get { lock (_gate) return _currentEpoch; } }

    // Materializes an immutable snapshot under the lock at call time - never a live
    // Dictionary.ValueCollection, which would not be safe to enumerate while a concurrent writer
    // (ApplySnapshot/ApplyEntry) mutates the same dictionary on another thread. A caller that needs
    // a fresh view after the next feed poll must call this again, matching this project's
    // established "atomic snapshot, re-read when needed" convention (see WorldMonsterActorView's
    // own doc comment).
    public ImmutableArray<WorldMonsterInstance> AllInstances { get { lock (_gate) return [.. _byActorId.Values]; } }

    public bool TryGetInstance(uint actorId, out WorldMonsterInstance instance)
    {
        lock (_gate) return _byActorId.TryGetValue(actorId, out instance!);
    }

    public WorldMonsterEngagementState EngagementOf(uint actorId)
    {
        lock (_gate) return _engagementByActorId.GetValueOrDefault(actorId, WorldMonsterEngagementState.Unengaged);
    }

    // Atomic combined lookup - epoch and instance are captured under the SAME lock acquisition, so
    // a caller building a WorldMonsterLifeReference (which requires both) never observes a torn
    // read (e.g. an instance from one epoch paired with a CurrentEpoch that has since moved on to a
    // fresh resync). Every call site that previously read CurrentEpoch and TryGetInstance/TryGet as
    // two separate operations for the SAME actor lookup must use this instead.
    public bool TryGetLife(uint actorId, out WorldSimulationEpoch epoch, out WorldMonsterInstance instance)
    {
        lock (_gate)
        {
            if (_currentEpoch is { } currentEpoch && _byActorId.TryGetValue(actorId, out var found))
            {
                epoch = currentEpoch;
                instance = found;
                return true;
            }
        }
        epoch = default;
        instance = null!;
        return false;
    }

    // Atomic cadence-evaluation snapshot for MonsterAttackCadenceExecutor.ProcessAsync - captures
    // CurrentEpoch, every tracked instance, AND its own engagement state from ONE lock acquisition,
    // eliminating the old three-separate-dictionary-read pattern (CurrentEpoch, then AllInstances,
    // then a per-instance EngagementOf call) that could otherwise race a concurrent ApplySnapshot/
    // ApplyEntry mutation between any of those steps.
    public bool SnapshotForCadence(out WorldSimulationEpoch epoch, out ImmutableArray<(WorldMonsterInstance Instance, WorldMonsterEngagementState Engagement)> instances)
    {
        lock (_gate)
        {
            if (_currentEpoch is not { } currentEpoch)
            {
                epoch = default;
                instances = [];
                return false;
            }
            epoch = currentEpoch;
            var builder = ImmutableArray.CreateBuilder<(WorldMonsterInstance, WorldMonsterEngagementState)>(_byActorId.Count);
            foreach (var (actorId, instance) in _byActorId)
                builder.Add((instance, _engagementByActorId.GetValueOrDefault(actorId, WorldMonsterEngagementState.Unengaged)));
            instances = builder.MoveToImmutable();
            return true;
        }
    }

    // Applies an atomic bootstrap or full resync snapshot (WorldMonsterFeedPage.Status is Ready
    // with a non-null Snapshot, OR ResyncRequired) - step 1-3 of the binding ordering above; the
    // caller is responsible for step 4 (session reconciliation, needs socket/session state this
    // type deliberately has no access to) and step 5 (advancing Cursor, via CommitCursor below -
    // never done automatically by this method, since the caller must confirm step 4 succeeded first).
    //
    // Same life (MapId+Epoch+ActorId+IncarnationId unchanged) -> combat state is preserved
    // untouched (a damaged monster's HP must never reset merely because a resync/bootstrap
    // happened to re-observe it). New incarnation -> fresh full-HP combat state, resetting cadence.
    // New epoch (different from CurrentEpoch) -> EVERY old-epoch combat-state entry for this map is
    // discarded outright before the new snapshot's own entries are registered - never merged.
    // A World-reported DEAD life observed for the FIRST time (this map's projection has never seen
    // this ActorId before) never gets a fresh full-HP combat-state entry registered at all - only a
    // genuinely Alive first-observed life does.
    public void ApplySnapshot(IReadOnlyList<WorldMonsterInstance> snapshot, WorldSimulationEpoch epoch, MonsterCombatStateStore combatState)
    {
        lock (_gate)
        {
            if (_currentEpoch is { } previousEpoch && !previousEpoch.Equals(epoch))
                combatState.RemoveEpoch(MapId, previousEpoch);

            var snapshotByActorId = snapshot.ToDictionary(instance => instance.ActorId);
            // A life present in the OLD projection but absent from the fresh snapshot has vanished
            // from World's own perspective (died-and-was-reaped, or this map's simulation was
            // rebuilt without it) - its combat-state entry (if any, under the OLD epoch/incarnation)
            // is no longer reachable via any future TryGet using current identity, so it is removed
            // explicitly here rather than left to leak indefinitely. An ActorId present in BOTH the
            // old projection and the new snapshot, but under a DIFFERENT IncarnationId, is a
            // separate real gap this same loop must also cover: the OLD incarnation's combat-state
            // key is equally unreachable going forward and must be reaped here too, not left to a
            // vanished-actor-only check that would otherwise miss it entirely.
            foreach (var (actorId, previous) in _byActorId)
            {
                if (!snapshotByActorId.TryGetValue(actorId, out var current) || !current.IncarnationId.Equals(previous.IncarnationId))
                    combatState.Remove(new MonsterCombatKey(MapId, epoch, actorId, previous.IncarnationId));
            }

            _byActorId.Clear();
            _engagementByActorId.Clear();
            foreach (var instance in snapshot)
            {
                _byActorId[instance.ActorId] = instance;
                _engagementByActorId[instance.ActorId] = instance.Lifecycle == WorldMonsterLifecycleState.Alive ? instance.Engagement : WorldMonsterEngagementState.Unengaged;
                var key = new MonsterCombatKey(MapId, epoch, instance.ActorId, instance.IncarnationId);
                // Same life already registered (a resync re-observing an unchanged incarnation under
                // the unchanged epoch) -> preserve its existing CurrentHp/NextAttackAt untouched. A
                // genuinely new life (new incarnation, or first time this map's projection has ever
                // seen this ActorId) -> register fresh full-HP/no-cadence state, UNLESS this is a
                // first-ever observation of an ALREADY-DEAD life (Lifecycle == Dead) - such a life
                // gets no fresh HP entry registered at all, since it was never alive from this
                // projection's own perspective and a fresh full-HP entry would misrepresent it as a
                // live, undamaged monster until the next feed entry catches up. AttackRange/MaxHp
                // are not present in the wire projection's own WorldMonsterInstance record -
                // resolving the static MobDefinition here (once per snapshot entry) is the same
                // GeneratedMobRegistry-backed lookup WorldMonsterActorView itself performs.
                if (!combatState.TryGet(key, out _) && instance.Lifecycle == WorldMonsterLifecycleState.Alive)
                    combatState.Register(MapId, epoch, instance.ActorId, instance.IncarnationId, GeneratedMobRegistryLookup.MaxHpFor(instance.MobId));
            }

            _currentEpoch = epoch;
        }
    }

    // Applies one incremental feed entry (WorldMonsterFeedPage.Entries, when Status is Ready with a
    // non-null cursor-relative Entries list) - updates the shared projection and, for a Died/
    // Respawned transition, the combat-state store's own life-tracking, in feed order. Callers
    // process entries one at a time via this method (never batch-applied out of order) so a
    // Respawned entry for actorId X always lands strictly after that same actorId's own preceding
    // Died entry within one incremental page.
    //
    // Replay-safety: if a page's cursor was never committed (a crash/exception between apply and
    // commit) and the SAME entry is later replayed on a fresh poll, applying it again must be fully
    // idempotent - in particular, a replayed Respawned entry must never reset an already-damaged
    // life's HP a second time (see the Respawned case below for the exact TryGet-guarded fix).
    public void ApplyEntry(WorldMonsterFeedEntry entry, MonsterCombatStateStore combatState, WorldSimulationEpoch epoch)
    {
        lock (_gate)
        {
            var instance = entry.Instance;
            _byActorId.TryGetValue(instance.ActorId, out var previous);
            _byActorId[instance.ActorId] = instance;

            switch (entry.Kind)
            {
                case WorldMonsterFeedEntryKind.Died:
                    _engagementByActorId[instance.ActorId] = WorldMonsterEngagementState.Unengaged;
                    // Combat state for the dead life is intentionally LEFT IN PLACE (not removed) here -
                    // a consumer (the local attack-cadence executor, packet projection) may still need
                    // to read its final CurrentHp==0 for this same tick/poll cycle. It is removed once
                    // superseded: either the eventual Respawned entry registers a fresh entry under the
                    // new incarnation (the old key simply becomes unreachable), or a future resync's own
                    // vanished-life cleanup (see ApplySnapshot) reaps it if the monster never reappears.
                    break;

                case WorldMonsterFeedEntryKind.Respawned:
                    _engagementByActorId[instance.ActorId] = WorldMonsterEngagementState.Unengaged;
                    // Replay-safe: only register a fresh full-HP entry when this EXACT
                    // (MapId, Epoch, ActorId, IncarnationId) key is not already registered - a
                    // replayed Respawned entry (uncommitted cursor, retried poll) for a life that
                    // was already registered (and has possibly since taken damage) must be a no-op,
                    // never a second HP reset. Also reap the PREVIOUS incarnation's own combat-state
                    // key here (captured via the TryGetValue above, BEFORE _byActorId was overwritten
                    // with the new instance) - it is now permanently unreachable under the new
                    // incarnation identity and must not leak indefinitely.
                    if (previous is not null && !previous.IncarnationId.Equals(instance.IncarnationId))
                        combatState.Remove(new MonsterCombatKey(MapId, epoch, instance.ActorId, previous.IncarnationId));
                    var respawnKey = new MonsterCombatKey(MapId, epoch, instance.ActorId, instance.IncarnationId);
                    if (!combatState.TryGet(respawnKey, out _))
                        combatState.Register(MapId, epoch, instance.ActorId, instance.IncarnationId, GeneratedMobRegistryLookup.MaxHpFor(instance.MobId));
                    break;

                case WorldMonsterFeedEntryKind.EngagementAcquired:
                case WorldMonsterFeedEntryKind.ChaseStarted:
                case WorldMonsterFeedEntryKind.InAttackRange:
                    _engagementByActorId[instance.ActorId] = instance.Engagement;
                    break;

                case WorldMonsterFeedEntryKind.ChaseInterrupted:
                    _engagementByActorId[instance.ActorId] = instance.Engagement;
                    break;

                case WorldMonsterFeedEntryKind.TargetUnlocked:
                    _engagementByActorId[instance.ActorId] = WorldMonsterEngagementState.Unengaged;
                    break;

                case WorldMonsterFeedEntryKind.Moved:
                    // Position/IsWalking/destination already updated via the _byActorId assignment
                    // above - Moved carries no engagement-state implication of its own.
                    break;
            }
        }
    }

    // Step 5 of the binding ordering - advances Cursor ONLY after the caller has confirmed steps
    // 2-4 (projection, combat-state, and every active session's own client-visible reconciliation)
    // completed successfully. Never call this before that.
    public void CommitCursor(WorldSimulationEpoch epoch, long asOfSequence)
    {
        lock (_gate) _cursor = new WorldMonsterFeedCursor(epoch, asOfSequence);
    }
}

// Small, explicit static helper resolving a WorldMonsterInstance's own MobId against the same
// generated static mob table WorldMonsterActorView itself uses - kept as its own named helper
// (rather than inlined at each call site) so MonsterFeedProjection's own registration logic reads
// as "look up the static MaxHp for a fresh combat-state entry", matching WorldMonsterActorView's
// identical "fail loudly, never silently substitute a placeholder" contract for an unresolvable MobId.
internal static class GeneratedMobRegistryLookup
{
    public static uint MaxHpFor(int mobId) => (uint)Athena.Net.MapServer.Generated.GameData.Mobs.GeneratedMobRegistry.Get(mobId).MaxHp;
}
