using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 6 cutover: MapClientSession no longer accepts a MonsterRegistry (`monsters:`) at all - it
// reads monster visibility/movement from a World-authoritative MonsterFeedProjectionRegistry
// (`monsterProjections:`) instead (see MapClientSession's own constructor doc comment). Several
// packet/visibility-projection tests in this Net/ folder still find it useful to build a LOCAL
// MonsterRegistry/MobInstance purely to derive realistic position/identity/movement data (a real
// spawn, a real respawn cycle, deterministic placement via a fixed cell selector) - this helper
// bridges that local MobInstance state into a MonsterFeedProjectionRegistry the exact same way
// MonsterFeedProjection's own production ApplySnapshot reconciliation would, so MapClientSession's
// OWN packet-building/visibility logic (which still exists unchanged post-cutover) can be exercised
// end-to-end without a real Orleans World grain.
internal static class WorldMonsterProjectionTestHelper
{
    // Converts one MobInstance's CURRENT state into a WorldMonsterInstance wire-shaped snapshot,
    // mirroring WorldMonsterMapSimulation.ToWireInstance's own conversion on the real World side.
    public static WorldMonsterInstance ToWorldMonsterInstance(this MobInstance instance)
    {
        var position = instance.GetPosition();
        return new WorldMonsterInstance(
            ActorId: instance.ActorId,
            IncarnationId: new WorldMonsterIncarnationId(instance.IncarnationId.Value),
            MapId: instance.Map,
            MobId: instance.Spawn.Mob.Id,
            X: position.X,
            Y: position.Y,
            Lifecycle: instance.IsAlive ? WorldMonsterLifecycleState.Alive : WorldMonsterLifecycleState.Dead,
            IsWalking: instance.IsWalking,
            DestinationX: instance.MovementDestination.X,
            DestinationY: instance.MovementDestination.Y,
            Engagement: WorldMonsterEngagementState.Unengaged,
            EngagedTarget: null);
    }

    // Seeds a fresh MonsterFeedProjectionRegistry with the given instances' CURRENT state, one
    // ApplySnapshot call per distinct map (ApplySnapshot is scoped to a single MonsterFeedProjection,
    // one per map) - a throwaway MonsterCombatStateStore is used here since these callers only need
    // the projection's own position/identity/movement data for MapClientSession's visibility/movement
    // packet-building path, never this store's own HP bookkeeping (a fresh, unrelated epoch per map
    // is fine for exactly that reason). Callers that DO care about HP/combat-state continuity
    // (anything driving a real attack through the session) must use the (epoch, combatState)
    // overload below instead, so the projection's epoch matches the store's own registrations.
    public static MonsterFeedProjectionRegistry SeedProjectionRegistry(params IEnumerable<MobInstance> instances)
    {
        var registry = new MonsterFeedProjectionRegistry();
        var byMap = instances.GroupBy(instance => instance.Map, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byMap)
        {
            var projection = registry.GetOrCreate(group.Key);
            projection.ApplySnapshot(group.Select(ToWorldMonsterInstance).ToArray(), WorldSimulationEpoch.NewEpoch(), new MonsterCombatStateStore());
        }
        return registry;
    }

    // Seeds (or re-seeds) ONE map's projection from the given instances' CURRENT state, under the
    // SAME epoch and MonsterCombatStateStore the caller's own combat setup already uses - required
    // for any test that drives a real attack through MapClientSession's own socket path (its
    // internal TryGetProjectedMonster/life-reference construction reads this exact epoch), and for
    // tests that mutate a local MobInstance's position/movement mid-test (e.g. simulating the
    // Poring "walking away" mid-repeat-attack) and need the LIVE projection - never a value
    // captured at seed time - to reflect it on MapClientSession's next read. Passing the SAME epoch
    // (rather than minting a fresh one, which ApplySnapshot's own new-epoch branch would otherwise
    // discard the store's existing entries for) is deliberate: a fresh epoch would invalidate every
    // MonsterCombatKey already registered, turning the very next attack into a spurious StaleLife
    // rejection.
    public static MonsterFeedProjectionRegistry SeedProjection(string mapId, WorldSimulationEpoch epoch, MonsterCombatStateStore combatState, IEnumerable<MobInstance> instances)
    {
        var registry = new MonsterFeedProjectionRegistry();
        ResyncProjection(registry, mapId, epoch, combatState, instances);
        return registry;
    }

    public static void ResyncProjection(MonsterFeedProjectionRegistry registry, string mapId, WorldSimulationEpoch epoch, MonsterCombatStateStore combatState, IEnumerable<MobInstance> instances)
    {
        var projection = registry.GetOrCreate(mapId);
        projection.ApplySnapshot(instances.Select(ToWorldMonsterInstance).ToArray(), epoch, combatState);
    }
}

// Minimal fake IWorldRuntime for tests that exercise MapClientSession's own real attack wire path
// (PerformDueRepeatAttackAsync/HandleIroAttackRequestAsync) end-to-end, which now requires a
// non-null _distributedWorld for TryMarkMonsterDeadAsync (the kill confirmation) and
// NotifyMonsterAttackedAsync (the non-lethal target-acquisition signal) - see
// MonsterCombatCoordinator's own doc comment on why those two RPCs moved to World post-cutover.
// Also implements RegisterPresenceAsync/MovePlayerAsync with the same minimal in-memory semantics
// as MapTcpServer's own private InMemoryTestWorldRuntime (not reusable directly - it is private to
// that class), since a real socket test that drives BOTH an attack and a subsequent movement
// packet through the same session needs ResolveWorldMovementTargetAsync's own `_distributedWorld is
// not null` branch to succeed rather than throw for "no World presence identity" - see
// HandleIroMovementAsync's own doc comment. Every other member throws NotSupportedException since
// this fake's only job is driving the attack/death/movement path deterministically for a SINGLE
// local (non-Orleans) test session, never a full transfer/truncation/advance-movement scenario.
internal sealed class FakeCombatWorldRuntime : IWorldRuntime
{
    private readonly HashSet<(string MapId, uint ActorId, long IncarnationId)> _confirmedDead = [];
    private readonly Dictionary<uint, WorldPlayerPresence> _presences = [];
    private readonly Dictionary<uint, (Guid Id, WorldPosition[] Path)> _movements = [];
    private readonly Lock _gate = new();

    // Null (the default) means "use the real Add-to-set semantics below" (MarkedDead the first
    // time, AlreadyDead thereafter) - the existing behavior every pre-existing test in this file
    // already depends on. Settable to a fixed status (typically StaleLifeReference) so a test can
    // script World rejecting the death confirmation outright, proving MapClientSession's own item-1
    // fail-closed lethal-wire-ordering handling (no damage/HP/death-vanish/EXP/quest-drop for a
    // rejected death) without needing a real incarnation/epoch mismatch to trigger it.
    public WorldMonsterDeathStatus? TryMarkMonsterDeadStatusOverride { get; set; }

    // Item 2 of the Step 6 final correctness pass: throws a transient (IOException-shaped) failure
    // for the FIRST N calls (decremented per call), then falls through to the ordinary
    // override/Add-to-set behavior - proves MapClientSession's own transient-World-RPC-failure
    // handling (log, leave HP untouched, re-arm the ordinary attack cadence, keep the repeat-attack
    // loop alive) without needing a real Orleans transport failure.
    private int _throwTransientTryMarkMonsterDeadCount;
    public int ThrowTransientTryMarkMonsterDeadCount { set => _throwTransientTryMarkMonsterDeadCount = value; }
    public int TryMarkMonsterDeadCallCount { get; private set; }

    public Task<WorldMonsterDeathResult> TryMarkMonsterDeadAsync(WorldMonsterLifeReference reference, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            TryMarkMonsterDeadCallCount++;
            if (_throwTransientTryMarkMonsterDeadCount > 0)
            {
                _throwTransientTryMarkMonsterDeadCount--;
                throw new IOException("Simulated transient World RPC failure.");
            }
        }

        if (TryMarkMonsterDeadStatusOverride is { } overrideStatus) return Task.FromResult(new WorldMonsterDeathResult(overrideStatus));

        var key = (reference.MapId, reference.ActorId, reference.IncarnationId.Value);
        lock (_gate)
        {
            var status = _confirmedDead.Add(key) ? WorldMonsterDeathStatus.MarkedDead : WorldMonsterDeathStatus.AlreadyDead;
            return Task.FromResult(new WorldMonsterDeathResult(status));
        }
    }

    public bool IsConfirmedDead(WorldMonsterLifeReference reference)
    {
        lock (_gate) return _confirmedDead.Contains((reference.MapId, reference.ActorId, reference.IncarnationId.Value));
    }

    // Defaults to Acquired (the existing behavior every pre-existing test in this file already
    // depends on) - settable so a test can script a non-success status (StaleLifeReference,
    // StaleAttackerPresence, MonsterNotAttackable, AttackerNotEngageable) to prove MapClientSession's
    // own fail-closed handling of a rejected non-lethal engagement-acquisition result (item 8 of the
    // Step 6 hardening pass).
    public WorldMonsterAttackedStatus NotifyMonsterAttackedStatusOverride { get; set; } = WorldMonsterAttackedStatus.Acquired;

    // Item 2 of the Step 6 final correctness pass: same transient-failure-once shape as
    // ThrowTransientTryMarkMonsterDeadCount above, for the non-lethal engagement-acquisition RPC.
    private int _throwTransientNotifyMonsterAttackedCount;
    public int ThrowTransientNotifyMonsterAttackedCount { set => _throwTransientNotifyMonsterAttackedCount = value; }
    public int NotifyMonsterAttackedCallCount { get; private set; }

    public Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            NotifyMonsterAttackedCallCount++;
            if (_throwTransientNotifyMonsterAttackedCount > 0)
            {
                _throwTransientNotifyMonsterAttackedCount--;
                throw new IOException("Simulated transient World RPC failure.");
            }
        }
        return Task.FromResult(new WorldMonsterAttackedResult(NotifyMonsterAttackedStatusOverride));
    }

    public Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _presences[presence.CharacterId] = presence with { MapId = mapId };
            return Task.FromResult(new WorldPresenceRegistration("test-partition", mapId, WorldPresenceRegistrationStatus.Registered, _presences.Count));
        }
    }

    public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var status = _presences.Remove(characterId) ? WorldPresenceUnregistrationStatus.Removed : WorldPresenceUnregistrationStatus.AlreadyAbsent;
            return Task.FromResult(new WorldPresenceUnregistration("test-partition", mapId, status, _presences.Count));
        }
    }

    public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_presences.TryGetValue(command.CharacterId, out var current))
                return Task.FromResult(new WorldMovementResult(WorldMovementStatus.NotFound, null));
            if (current.PresenceId != command.PresenceId)
                return Task.FromResult(new WorldMovementResult(WorldMovementStatus.PresenceMismatch, current));
            var movementId = Guid.NewGuid();
            WorldPosition[] path = [new(command.FromX, command.FromY), new(command.DestinationX, command.DestinationY)];
            _movements[command.CharacterId] = (movementId, path);
            return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Moved, current, path, movementId));
        }
    }

    public Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_presences.TryGetValue(command.CharacterId, out var current))
                return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.NotFound, null));
            if (current.PresenceId != command.PresenceId)
                return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.PresenceMismatch, current));
            if (current.X != command.ExpectedX || current.Y != command.ExpectedY)
                return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.SourceMismatch, current));
            if (!_movements.TryGetValue(command.CharacterId, out var movement) || movement.Id != command.MovementId)
                return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.StaleRoute, current));
            var advanced = current with { X = command.NewX, Y = command.NewY };
            _presences[command.CharacterId] = advanced;
            var currentIndex = Array.FindIndex(movement.Path, cell => cell.X == current.X && cell.Y == current.Y);
            if (currentIndex >= 0 && currentIndex + 1 == movement.Path.Length - 1) _movements.Remove(command.CharacterId);
            return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.Advanced, advanced));
        }
    }

    public Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_presences.TryGetValue(command.CharacterId, out var current))
                return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.PresenceNotFound, null));
            if (current.PresenceId != command.PresenceId)
                return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.PresenceMismatch, current));
            if (!_movements.TryGetValue(command.CharacterId, out var movement))
                return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.AlreadyAbsent, current));
            if (movement.Id != command.MovementId)
                return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.SourceMismatch, current));
            _movements.Remove(command.CharacterId);
            return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.Cancelled, current));
        }
    }

    public Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command, CancellationToken cancellationToken) =>
        throw new NotSupportedException("FakeCombatWorldRuntime only supports the monster-attack/death/RegisterPresence/MovePlayer/AdvanceMovement/CancelMovement RPCs.");
    public Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken) =>
        throw new NotSupportedException("FakeCombatWorldRuntime only supports the monster-attack/death/RegisterPresence/MovePlayer/AdvanceMovement/CancelMovement RPCs.");
    public Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch, CancellationToken cancellationToken) =>
        throw new NotSupportedException("FakeCombatWorldRuntime only supports the monster-attack/death/RegisterPresence/MovePlayer RPCs.");
    public Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("FakeCombatWorldRuntime only supports the monster-attack/death/RegisterPresence/MovePlayer RPCs.");
    public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException("FakeCombatWorldRuntime only supports the monster-attack/death/RegisterPresence/MovePlayer RPCs.");
    // Item 6 of the Step 6 correctness-hardening pass: scriptable so a test can simulate a transient
    // RPC failure on the FIRST N attempts (via ThrowTransientFailureCount, decremented on each call
    // that throws) and confirm a LATER retry - driven by MapClientSession.TryReconcilePendingLifeStateAsync,
    // called every tick regardless of whether a new transition happened - eventually succeeds without
    // any further local life transition ever occurring. Every call (thrown or not) is counted in
    // UpdatePresenceLifeStateCallCount so a test can assert "was retried" vs. "was never called
    // again" (e.g. after a StalePresence result retires the pending update).
    private int _throwTransientFailureCount;
    public int ThrowTransientFailureCount { set => _throwTransientFailureCount = value; }
    public int UpdatePresenceLifeStateCallCount { get; private set; }
    public WorldPresenceLifeStateStatus UpdatePresenceLifeStateStatusOverride { get; set; } = WorldPresenceLifeStateStatus.Updated;

    public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            UpdatePresenceLifeStateCallCount++;
            if (_throwTransientFailureCount > 0)
            {
                _throwTransientFailureCount--;
                throw new IOException("Simulated transient World RPC failure.");
            }
            return Task.FromResult(new WorldPresenceLifeStateResult(UpdatePresenceLifeStateStatusOverride));
        }
    }
}
