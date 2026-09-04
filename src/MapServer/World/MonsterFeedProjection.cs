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
public sealed class MonsterFeedProjection(string mapId)
{
    private readonly Dictionary<uint, WorldMonsterInstance> _byActorId = [];
    private readonly Dictionary<uint, WorldMonsterEngagementState> _engagementByActorId = [];

    public string MapId { get; } = mapId;
    public WorldMonsterFeedCursor? Cursor { get; private set; }
    public WorldSimulationEpoch? CurrentEpoch { get; private set; }
    public IReadOnlyCollection<WorldMonsterInstance> AllInstances => _byActorId.Values;
    public bool TryGetInstance(uint actorId, out WorldMonsterInstance instance) => _byActorId.TryGetValue(actorId, out instance!);
    public WorldMonsterEngagementState EngagementOf(uint actorId) => _engagementByActorId.GetValueOrDefault(actorId, WorldMonsterEngagementState.Unengaged);

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
    public void ApplySnapshot(IReadOnlyList<WorldMonsterInstance> snapshot, WorldSimulationEpoch epoch, MonsterCombatStateStore combatState)
    {
        if (CurrentEpoch is { } previousEpoch && !previousEpoch.Equals(epoch))
            combatState.RemoveEpoch(MapId, previousEpoch);

        var snapshotActorIds = new HashSet<uint>(snapshot.Select(instance => instance.ActorId));
        // A life present in the OLD projection but absent from the fresh snapshot has vanished
        // from World's own perspective (died-and-was-reaped, or this map's simulation was rebuilt
        // without it) - its combat-state entry (if any, under the OLD epoch/incarnation) is no
        // longer reachable via any future TryGet using current identity, so it is removed
        // explicitly here rather than left to leak indefinitely (matches "add explicit map/epoch
        // cleanup APIs rather than leaving stale entries indefinitely").
        foreach (var (actorId, previous) in _byActorId)
        {
            if (!snapshotActorIds.Contains(actorId))
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
            // seen this ActorId) -> register fresh full-HP/no-cadence state. AttackRange/MaxHp are
            // not present in the wire projection's own WorldMonsterInstance record - resolving the
            // static MobDefinition here (once per snapshot entry) is the same
            // GeneratedMobRegistry-backed lookup WorldMonsterActorView itself performs.
            if (!combatState.TryGet(key, out _))
                combatState.Register(MapId, epoch, instance.ActorId, instance.IncarnationId, GeneratedMobRegistryLookup.MaxHpFor(instance.MobId));
        }

        CurrentEpoch = epoch;
    }

    // Applies one incremental feed entry (WorldMonsterFeedPage.Entries, when Status is Ready with a
    // non-null cursor-relative Entries list) - updates the shared projection and, for a Died/
    // Respawned transition, the combat-state store's own life-tracking, in feed order. Callers
    // process entries one at a time via this method (never batch-applied out of order) so a
    // Respawned entry for actorId X always lands strictly after that same actorId's own preceding
    // Died entry within one incremental page.
    public void ApplyEntry(WorldMonsterFeedEntry entry, MonsterCombatStateStore combatState, WorldSimulationEpoch epoch)
    {
        var instance = entry.Instance;
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

    // Step 5 of the binding ordering - advances Cursor ONLY after the caller has confirmed steps
    // 2-4 (projection, combat-state, and every active session's own client-visible reconciliation)
    // completed successfully. Never call this before that.
    public void CommitCursor(WorldSimulationEpoch epoch, long asOfSequence) => Cursor = new WorldMonsterFeedCursor(epoch, asOfSequence);
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
