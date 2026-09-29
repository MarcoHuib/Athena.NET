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
            EngagedTarget: null,
            CurrentHp: instance.CurrentHp,
            MaxHp: instance.Spawn.Mob.MaxHp);
    }

    // Seeds a fresh MonsterFeedProjectionRegistry with the given instances' CURRENT state, one
    // ApplySnapshot call per distinct map (ApplySnapshot is scoped to a single MonsterFeedProjection,
    // one per map) - a throwaway MonsterAttackCadenceStore is used here since these callers only need
    // the projection's own position/identity/movement data for MapClientSession's visibility/movement
    // packet-building path, never this store's own cadence bookkeeping (a fresh, unrelated epoch per
    // map is fine for exactly that reason). Callers that DO care about combat-state continuity
    // (anything driving a real attack through the session) must use the (epoch, combatState)
    // overload below instead, so the projection's epoch matches the store's own registrations.
    public static MonsterFeedProjectionRegistry SeedProjectionRegistry(params IEnumerable<MobInstance> instances)
    {
        var registry = new MonsterFeedProjectionRegistry();
        var byMap = instances.GroupBy(instance => instance.Map, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byMap)
        {
            var projection = registry.GetOrCreate(group.Key);
            projection.ApplySnapshot(group.Select(ToWorldMonsterInstance).ToArray(), WorldSimulationEpoch.NewEpoch(), new MonsterAttackCadenceStore());
        }
        return registry;
    }

    // Seeds (or re-seeds) ONE map's projection from the given instances' CURRENT state, under the
    // SAME epoch and MonsterAttackCadenceStore the caller's own combat setup already uses - required
    // for any test that drives a real attack through MapClientSession's own socket path (its
    // internal TryGetProjectedMonster/life-reference construction reads this exact epoch), and for
    // tests that mutate a local MobInstance's position/movement mid-test (e.g. simulating the
    // Poring "walking away" mid-repeat-attack) and need the LIVE projection - never a value
    // captured at seed time - to reflect it on MapClientSession's next read. Passing the SAME epoch
    // (rather than minting a fresh one, which ApplySnapshot's own new-epoch branch would otherwise
    // discard the store's existing entries for) is deliberate: a fresh epoch would invalidate every
    // MonsterCombatKey already registered, turning the very next attack into a spurious StaleLife
    // rejection.
    //
    // Substep 9 (§7): also seeds `fakeWorld` (when supplied) with SeedMonster for every instance, at
    // the same HP the projection itself carries - keeps the client-visible projection and the fake
    // World's own damage-ledger HP from ever silently drifting apart. Optional (null) for callers
    // that only need position/visibility projection and never drive a real ApplyMonsterDamageAsync
    // call through the session.
    public static MonsterFeedProjectionRegistry SeedProjection(string mapId, WorldSimulationEpoch epoch, MonsterAttackCadenceStore combatState, IEnumerable<MobInstance> instances, FakeCombatWorldRuntime? fakeWorld = null)
    {
        var registry = new MonsterFeedProjectionRegistry();
        ResyncProjection(registry, mapId, epoch, combatState, instances, fakeWorld);
        return registry;
    }

    public static void ResyncProjection(MonsterFeedProjectionRegistry registry, string mapId, WorldSimulationEpoch epoch, MonsterAttackCadenceStore combatState, IEnumerable<MobInstance> instances, FakeCombatWorldRuntime? fakeWorld = null)
    {
        var projection = registry.GetOrCreate(mapId);
        var materialized = instances.ToArray();
        projection.ApplySnapshot(materialized.Select(ToWorldMonsterInstance).ToArray(), epoch, combatState);
        if (fakeWorld is null) return;
        foreach (var instance in materialized)
        {
            var life = new WorldMonsterLifeReference(mapId, epoch, instance.ActorId, new WorldMonsterIncarnationId(instance.IncarnationId.Value));
            fakeWorld.SeedMonster(life, instance.CurrentHp, instance.Spawn.Mob.MaxHp);
        }
    }
}

// Minimal fake IWorldRuntime for tests that exercise MapClientSession's own real attack wire path
// (PerformDueRepeatAttackAsync/HandleIroAttackRequestAsync) end-to-end, which now requires a
// non-null _distributedWorld for ApplyMonsterDamageAsync (the sole HP-mutation/kill-confirmation
// seam) and NotifyMonsterAttackedAsync (the non-lethal target-acquisition signal) - see
// MonsterCombatCoordinator's own doc comment on why those two RPCs live on World post-cutover.
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
    private readonly Dictionary<uint, WorldPlayerPresence> _presences = [];
    private readonly Dictionary<uint, WorldPlayerPublicState> _publicStateByCharacterId = [];
    private readonly Dictionary<uint, (Guid Id, WorldPosition[] Path)> _movements = [];
    private readonly Lock _gate = new();
    // Item 14: a fixed epoch for this fake's ENTIRE lifetime - real World semantics (epoch
    // rotation, bounded retention, incremental entries) are deliberately NOT reproduced here (see
    // this class's own top-of-file doc comment: it only drives the attack/death/movement path
    // deterministically for a single local test session). PollPlayerFeedAsync below always returns
    // a fresh full Snapshot bootstrap - correct for any cursor (null OR non-null, since a
    // never-changing epoch means a non-null cursor's own Sequence can never legitimately fall
    // outside this always-current snapshot) - so MapTcpServer's own per-map tick loop can discover
    // every registered player without needing real incremental-entry semantics.
    private static readonly WorldSimulationEpoch PlayerFeedEpoch = WorldSimulationEpoch.NewEpoch();

    // Substep 9 (§7): two separate ledgers, matching the real World's own ownership split - HP/
    // Alive-Dead state keyed by WorldMonsterLifeReference alone (shared across attackers, exactly
    // like the real WorldMonsterMapSimulation's own per-life HP ledger), sequence/idempotency state
    // keyed by (AttackerCharacterId, AttackerPresenceId, Life) matching WorldMonsterDamageCommand's
    // own idempotency key exactly.
    private sealed class FakeMonsterHpState
    {
        public uint CurrentHp;
        public uint MaxHp;
    }

    private sealed record FakeAttackSequenceState(long Sequence, uint Damage, bool AcquireEngagement, WorldMonsterDamageResult Result);

    private readonly Dictionary<WorldMonsterLifeReference, FakeMonsterHpState> _hpByLife = [];
    private readonly Dictionary<(uint CharacterId, Guid PresenceId, WorldMonsterLifeReference Life), FakeAttackSequenceState> _sequenceByAttackerAndLife = [];

    // Explicit seeding - WorldMonsterDamageCommand carries no MobId/CurrentHp/MaxHp, so the fake
    // cannot invent a realistic Applied result from the command alone. The first call for a given
    // Life creates its entry; reseeding the SAME Life only where a test explicitly models a fresh
    // authoritative snapshot/resync (never an implicit side effect of some other call). A new
    // incarnation is a new Life and gets a fresh, independently-seeded entry - never inherits the
    // prior incarnation's HP. Sequence state remains keyed separately by (attacker, life), untouched
    // by reseeding HP.
    public void SeedMonster(WorldMonsterLifeReference life, uint currentHp, uint maxHp)
    {
        lock (_gate) { _hpByLife[life] = new FakeMonsterHpState { CurrentHp = currentHp, MaxHp = maxHp }; }
    }

    // Test-only read of the fake ledger's own current HP for a life - lets a test assert HP
    // continuity/no-double-hit against the SAME authority ApplyMonsterDamageAsync itself mutates,
    // now that MapClientSession no longer mirrors HP in any local store.
    public uint? TryGetCurrentHp(WorldMonsterLifeReference life)
    {
        lock (_gate) return _hpByLife.TryGetValue(life, out var state) ? state.CurrentHp : null;
    }

    // Step 6 final correctness pass, item 1's own race tests: an optional hook invoked immediately
    // BEFORE ApplyMonsterDamageAsync returns (its normal result OR its scripted exception) - lets a
    // test simulate "World's independent Died feed reaches this same session's
    // NotifyMonsterDiedAsync WHILE this exact RPC call is still in flight" at the precise moment
    // this arbitration race requires, without needing genuine multi-threaded timing.
    public Func<Task>? BeforeApplyMonsterDamageReturns { get; set; }

    // Contract/plumbing wiring - a minimal scriptable fake matching this file's existing
    // NotifyMonsterAttackedAsync scripting shape (override status, call count, transient-throw-N-
    // times), taking priority over the ledger below when set.
    public WorldMonsterDamageResult? ApplyMonsterDamageResultOverride { get; set; }
    private int _throwTransientApplyMonsterDamageCount;
    public int ThrowTransientApplyMonsterDamageCount { set => _throwTransientApplyMonsterDamageCount = value; }
    public int ApplyMonsterDamageCallCount { get; private set; }
    public WorldMonsterDamageCommand? LastApplyMonsterDamageCommand { get; private set; }

    public async Task<WorldMonsterDamageResult> ApplyMonsterDamageAsync(WorldMonsterDamageCommand command, CancellationToken cancellationToken)
    {
        bool shouldThrow;
        lock (_gate)
        {
            ApplyMonsterDamageCallCount++;
            LastApplyMonsterDamageCommand = command;
            shouldThrow = _throwTransientApplyMonsterDamageCount > 0;
            if (shouldThrow) _throwTransientApplyMonsterDamageCount--;
        }

        if (BeforeApplyMonsterDamageReturns is { } hook) await hook();

        if (shouldThrow) throw new IOException("Simulated transient World RPC failure.");
        if (ApplyMonsterDamageResultOverride is { } overrideResult) return overrideResult;

        lock (_gate)
        {
            // Step 1: absent HP entry -> StaleLifeReference. No sequence-table access before this
            // check.
            if (!_hpByLife.TryGetValue(command.Life, out var hpState))
                return new WorldMonsterDamageResult(WorldMonsterDamageStatus.StaleLifeReference, 0, 0, 0, false, null);

            var sequenceKey = (command.AttackerCharacterId, command.AttackerPresenceId, command.Life);
            // Step 2: an existing sequence-ledger entry for this attacker+life short-circuits BEFORE
            // any liveness check - a lethal hit's own lost-and-replayed response must still replay
            // correctly even if the shared HP entry now shows the monster dead.
            if (_sequenceByAttackerAndLife.TryGetValue(sequenceKey, out var existing))
            {
                if (command.AttackSequence == existing.Sequence)
                {
                    return command.Damage == existing.Damage && command.AcquireEngagement == existing.AcquireEngagement
                        ? existing.Result with { Status = WorldMonsterDamageStatus.ReplayedSequence }
                        : new WorldMonsterDamageResult(WorldMonsterDamageStatus.Conflict, existing.Result.HpBefore, existing.Result.HpAfter, hpState.MaxHp, false, null);
                }
                if (command.AttackSequence < existing.Sequence)
                    return new WorldMonsterDamageResult(WorldMonsterDamageStatus.StaleSequence, existing.Result.HpBefore, existing.Result.HpAfter, hpState.MaxHp, false, null);
                // Higher sequence than stored - falls through as a genuinely new attempt.
            }

            // Step 3: only NOW check liveness - a genuinely new attempt (higher/no prior sequence)
            // against an already-dead life is rejected WITHOUT recording into the sequence ledger, so
            // a later exact replay of an earlier accepted sequence still correctly returns
            // ReplayedSequence.
            if (hpState.CurrentHp == 0)
                return new WorldMonsterDamageResult(WorldMonsterDamageStatus.AlreadyDead, 0, 0, hpState.MaxHp, false, null);

            // Step 4: apply the clamped subtract to the SHARED hp entry, then record the accepted
            // sequence - recording only after damage has actually been computed and applied.
            var hpBefore = hpState.CurrentHp;
            var hpAfter = command.Damage >= hpBefore ? 0u : hpBefore - command.Damage;
            hpState.CurrentHp = hpAfter;
            var killed = hpAfter == 0;
            WorldMonsterAttackedStatus? engagement = command.AcquireEngagement ? WorldMonsterAttackedStatus.Acquired : null;
            var result = new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, hpBefore, hpAfter, hpState.MaxHp, killed, engagement);
            _sequenceByAttackerAndLife[sequenceKey] = new FakeAttackSequenceState(command.AttackSequence, command.Damage, command.AcquireEngagement, result);
            return result;
        }
    }

    // Defaults to Acquired (the existing behavior every pre-existing test in this file already
    // depends on) - settable so a test can script a non-success status (StaleLifeReference,
    // StaleAttackerPresence, MonsterNotAttackable, AttackerNotEngageable) to prove MapClientSession's
    // own fail-closed handling of a rejected non-lethal engagement-acquisition result (item 8 of the
    // Step 6 hardening pass). Ignored entirely when StrictPresenceValidation is true (see that
    // field's own doc comment) - the strict path derives its own status from the ACTUAL registered
    // presence instead of a scripted override.
    public WorldMonsterAttackedStatus NotifyMonsterAttackedStatusOverride { get; set; } = WorldMonsterAttackedStatus.Acquired;

    // Live-acceptance identity-bug regression: when true, NotifyMonsterAttackedAsync stops
    // returning the scripted NotifyMonsterAttackedStatusOverride unconditionally and instead
    // genuinely validates command.AttackerCharacterId/AttackerPresenceId against whatever presence
    // was actually registered via RegisterPresenceAsync - returning StaleAttackerPresence for a
    // CharacterId/PresenceId that does not match a real registered presence, exactly like the real
    // WorldPartitionGrain does. This exists specifically so a test proving the CharacterId-vs-
    // AccountId identity fix cannot pass merely because a lenient fake blindly returns Acquired for
    // whatever value it's handed - see MapClientSessionAttackerIdentityTests.cs for the tests that
    // require this validation to be genuine.
    public bool StrictPresenceValidation { get; set; }
    public WorldMonsterAttackedCommand? LastNotifyMonsterAttackedCommand { get; private set; }

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
            LastNotifyMonsterAttackedCommand = command;
            if (_throwTransientNotifyMonsterAttackedCount > 0)
            {
                _throwTransientNotifyMonsterAttackedCount--;
                throw new IOException("Simulated transient World RPC failure.");
            }

            if (StrictPresenceValidation)
            {
                var status = _presences.TryGetValue(command.AttackerCharacterId, out var registered) && registered.PresenceId == command.AttackerPresenceId
                    ? WorldMonsterAttackedStatus.Acquired
                    : WorldMonsterAttackedStatus.StaleAttackerPresence;
                return Task.FromResult(new WorldMonsterAttackedResult(status));
            }
        }
        return Task.FromResult(new WorldMonsterAttackedResult(NotifyMonsterAttackedStatusOverride));
    }

    public Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, WorldPlayerPublicState publicState, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _presences[presence.CharacterId] = presence with { MapId = mapId };
            _publicStateByCharacterId[presence.CharacterId] = publicState;
            return Task.FromResult(new WorldPresenceRegistration("test-partition", mapId, WorldPresenceRegistrationStatus.Registered, _presences.Count));
        }
    }

    public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var status = _presences.Remove(characterId) ? WorldPresenceUnregistrationStatus.Removed : WorldPresenceUnregistrationStatus.AlreadyAbsent;
            _publicStateByCharacterId.Remove(characterId);
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
    // Live-acceptance identity-bug regression: records the exact update this fake most recently
    // received, so a test can assert its CharacterId is the REAL World CharacterId (never the
    // account/actor id) - see MapClientSessionAttackerIdentityTests.cs.
    public WorldPresenceLifeStateUpdate? LastUpdatePresenceLifeStateUpdate { get; private set; }

    public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            UpdatePresenceLifeStateCallCount++;
            LastUpdatePresenceLifeStateUpdate = update;
            if (_throwTransientFailureCount > 0)
            {
                _throwTransientFailureCount--;
                throw new IOException("Simulated transient World RPC failure.");
            }
            return Task.FromResult(new WorldPresenceLifeStateResult(UpdatePresenceLifeStateStatusOverride));
        }
    }

    public Task<WorldPlayerFeedPage> PollPlayerFeedAsync(WorldPlayerFeedCursor? cursor, string mapId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var snapshot = _presences.Values
                .Where(presence => string.Equals(presence.MapId, mapId, StringComparison.OrdinalIgnoreCase))
                .Select(presence =>
                {
                    var publicState = _publicStateByCharacterId.TryGetValue(presence.CharacterId, out var ps) ? ps : EmptyPublicState;
                    return new WorldPlayerPresenceEntry(presence, publicState);
                })
                .ToArray();
            return Task.FromResult(new WorldPlayerFeedPage(mapId, PlayerFeedEpoch, WorldPlayerFeedStatus.Ready, snapshot, Entries: null, AsOfSequence: 0));
        }
    }

    private static readonly WorldPlayerPublicState EmptyPublicState = new("", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public Task<WorldPlayerLookUpdateResult> UpdatePlayerLookAsync(string mapId, uint characterId, Guid presenceId, byte direction, byte headDirection, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_publicStateByCharacterId.TryGetValue(characterId, out var current)) return Task.FromResult(new WorldPlayerLookUpdateResult(WorldPlayerLookUpdateStatus.NotFound));
            _publicStateByCharacterId[characterId] = current with { Direction = direction, HeadDirection = headDirection };
            return Task.FromResult(new WorldPlayerLookUpdateResult(WorldPlayerLookUpdateStatus.Updated));
        }
    }

    public Task<WorldPlayerPublicStateUpdateResult> UpdatePlayerPublicStateAsync(string mapId, uint characterId, Guid presenceId, WorldPlayerPublicState publicState, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_presences.ContainsKey(characterId)) return Task.FromResult(new WorldPlayerPublicStateUpdateResult(WorldPlayerPublicStateUpdateStatus.NotFound));
            _publicStateByCharacterId[characterId] = publicState;
            return Task.FromResult(new WorldPlayerPublicStateUpdateResult(WorldPlayerPublicStateUpdateStatus.Updated));
        }
    }
    // Item 14 §3: real (not stubbed), matching MovePlayerAsync/AdvanceMovementAsync above -
    // MapClientSession.ResolveWorldMovementTargetAsync calls this unconditionally on every accepted
    // movement, and this fake IS used by tests that combine combat with ordinary movement (e.g.
    // movement cancelling an active repeat-attack) - it must not throw for those to keep working.
    public Task<WorldMovementProjectionResult> ConfirmMovementProjectionAsync(WorldMovementProjectionConfirmation confirmation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_movements.TryGetValue(confirmation.CharacterId, out var movement) || movement.Id != confirmation.MovementId)
                return Task.FromResult(new WorldMovementProjectionResult(WorldMovementProjectionStatus.SourceMismatch, null));
            return Task.FromResult(new WorldMovementProjectionResult(WorldMovementProjectionStatus.Confirmed, null));
        }
    }
    public Task<WorldMonsterAttackPublishResult> PublishMonsterAttackActionAsync(WorldMonsterAttackActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
}
