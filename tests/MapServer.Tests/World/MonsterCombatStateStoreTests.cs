using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.World;

// Step 6: MonsterCombatStateStore is keyed by the REAL World-issued (MapId, SimulationEpoch,
// ActorId, IncarnationId) tuple - these tests exercise the store directly (never through
// MonsterCombatCoordinator, never through a live MobInstance), proving its own key isolation,
// atomicity, and "report the HP==0 fact, never mutate any lifecycle state" contract hold
// independent of any caller. Post-cutover, the store has NO knowledge of MobInstance/lifecycle at
// all - a lethal ApplyDamage call only ever REPORTS KilledByThisHit=true; it is the caller's own
// responsibility (MonsterCombatCoordinator's orchestration layer) to then call World's
// TryMarkMonsterDeadAsync - see that RPC's own doc comment.
public sealed class MonsterCombatStateStoreTests
{
    private static WorldSimulationEpoch Epoch() => WorldSimulationEpoch.NewEpoch();
    private static WorldMonsterIncarnationId First => WorldMonsterIncarnationId.First;

    private static MonsterCombatKey Key(string mapId, WorldSimulationEpoch epoch, uint actorId, WorldMonsterIncarnationId incarnationId) =>
        new(mapId, epoch, actorId, incarnationId);

    [Fact]
    public void CombatState_IsIsolatedByMapId_SameActorIdDifferentEpoch_AreIndependentEntries()
    {
        var store = new MonsterCombatStateStore();
        var izludeEpoch = Epoch();
        var geffenEpoch = Epoch();
        store.Register("izlude", izludeEpoch, actorId: 1, First, maxHp: 55);
        store.Register("geffen", geffenEpoch, actorId: 1, First, maxHp: 55); // Same ActorId, different map/epoch - the key must still isolate it.

        store.ApplyDamage(Key("izlude", izludeEpoch, 1, First), damage: 10);

        Assert.True(store.TryGet(Key("izlude", izludeEpoch, 1, First), out var izludeState));
        Assert.Equal(45u, izludeState.CurrentHp);
        Assert.True(store.TryGet(Key("geffen", geffenEpoch, 1, First), out var geffenState));
        Assert.Equal(55u, geffenState.CurrentHp); // Untouched - damage to the "izlude" key must never leak to "geffen".
    }

    [Fact]
    public void CombatState_IsIsolatedByActorId_SameMapEpochDifferentActorId_AreIndependentEntries()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, actorId: 1, First, maxHp: 55);
        store.Register("int_land01", epoch, actorId: 2, First, maxHp: 55);

        store.ApplyDamage(Key("int_land01", epoch, 1, First), damage: 20);

        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out var stateA));
        Assert.Equal(35u, stateA.CurrentHp);
        Assert.True(store.TryGet(Key("int_land01", epoch, 2, First), out var stateB));
        Assert.Equal(55u, stateB.CurrentHp);
    }

    [Fact]
    public void CombatState_IsIsolatedByIncarnationId_NewIncarnationIsAnIndependentEntry()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, actorId: 1, First, maxHp: 55);
        store.ApplyDamage(Key("int_land01", epoch, 1, First), damage: 55); // Kill it (HP reaches 0).

        var newIncarnation = First.Next();
        store.Register("int_land01", epoch, actorId: 1, newIncarnation, maxHp: 55); // Respawn re-registration under the new incarnation.

        Assert.True(store.TryGet(Key("int_land01", epoch, 1, newIncarnation), out var freshState));
        Assert.Equal(55u, freshState.CurrentHp);
        Assert.Null(freshState.NextAttackAt);
        // The OLD incarnation's own key is a SEPARATE entry - unaffected by the new registration.
        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out var oldState));
        Assert.Equal(0u, oldState.CurrentHp);
    }

    [Fact]
    public void UnregisteredKey_CannotBeRead()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, actorId: 1, First, maxHp: 55);

        Assert.False(store.TryGet(Key("int_land01", epoch, actorId: 1, First.Next()), out _)); // Different (never-registered) incarnation.
        Assert.False(store.TryGet(Key("int_land01", Epoch(), actorId: 1, First), out _)); // Different (never-registered) epoch.
    }

    [Fact]
    public void StaleLife_CannotMutateCombatState_ApplyDamageReturnsStaleLife()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, actorId: 1, First, maxHp: 55);
        var staleKey = Key("int_land01", epoch, actorId: 1, First.Next());

        var result = store.ApplyDamage(staleKey, damage: 10);

        Assert.Equal(MonsterCombatDamageStatus.StaleLife, result.Status);
        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out var unchanged));
        Assert.Equal(55u, unchanged.CurrentHp); // Untouched by the rejected stale-life call.
    }

    [Fact]
    public void StaleLife_CannotScheduleNextAttack_SilentlyIgnored()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, actorId: 1, First, maxHp: 55);
        var staleKey = Key("int_land01", epoch, actorId: 1, First.Next());
        var dueAt = DateTimeOffset.UnixEpoch.AddSeconds(5);

        store.ScheduleNextAttack(staleKey, dueAt);

        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out var current));
        Assert.Null(current.NextAttackAt); // The stale-life schedule must not have landed on the current entry.
    }

    [Fact]
    public void ApplyDamage_MutatesExactlyOneCurrentHpSourceOfTruth()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 55);

        store.ApplyDamage(key, damage: 10);

        Assert.True(store.TryGet(key, out var state));
        Assert.Equal(45u, state.CurrentHp);
    }

    [Fact]
    public void LethalDamage_ReachesZeroExactlyOnce_ReportsKilledByThisHitOnlyOnce()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 55);

        var result = store.ApplyDamage(key, damage: 55);

        Assert.Equal(MonsterCombatDamageStatus.Applied, result.Status);
        Assert.Equal(0u, result.HpAfter);
        Assert.True(result.KilledByThisHit);

        // A second hit against the same (now HP==0) key must not report a second kill or go negative.
        var second = store.ApplyDamage(key, damage: 10);
        Assert.Equal(MonsterCombatDamageStatus.AlreadyDead, second.Status);
        Assert.False(second.KilledByThisHit);
        Assert.True(store.TryGet(key, out var finalState));
        Assert.Equal(0u, finalState.CurrentHp);
    }

    [Fact]
    public void DamageAfterDeath_CannotProduceAnotherLethalTransition()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 55);
        store.ApplyDamage(key, damage: 55);

        var result = store.ApplyDamage(key, damage: 1);

        Assert.Equal(MonsterCombatDamageStatus.AlreadyDead, result.Status);
        Assert.False(result.KilledByThisHit);
    }

    [Fact]
    public void NonLethalDamage_ClampsToZero_NeverUnderflows()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 55);

        var result = store.ApplyDamage(key, damage: 9999);

        Assert.Equal(0u, result.HpAfter);
        Assert.True(result.KilledByThisHit);
    }

    [Fact]
    public void NextAttackAt_IsStoredAndUpdatedOnlyInTheCombatStateStore()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 55);
        var dueAt = DateTimeOffset.UnixEpoch.AddMilliseconds(1872);

        store.ScheduleNextAttack(key, dueAt);

        Assert.True(store.TryGet(key, out var state));
        Assert.Equal(dueAt, state.NextAttackAt);
    }

    [Fact]
    public void NewIncarnation_ReceivesFreshFullHpAndFreshCadenceState()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, 1, First, maxHp: 55);
        store.ScheduleNextAttack(Key("int_land01", epoch, 1, First), DateTimeOffset.UnixEpoch.AddSeconds(2));
        store.ApplyDamage(Key("int_land01", epoch, 1, First), damage: 55);

        var newIncarnation = First.Next();
        store.Register("int_land01", epoch, 1, newIncarnation, maxHp: 55); // Fresh registration for the new incarnation (mirrors MapTcpServer's own Respawned feed-entry handling).

        Assert.True(store.TryGet(Key("int_land01", epoch, 1, newIncarnation), out var freshState));
        Assert.Equal(55u, freshState.CurrentHp);
        Assert.Equal(55u, freshState.MaxHp);
        Assert.Null(freshState.NextAttackAt);
    }

    [Fact]
    public void NewEpoch_DiscardsAllOldEpochCombatStateForThatMap()
    {
        var store = new MonsterCombatStateStore();
        var oldEpoch = Epoch();
        store.Register("int_land01", oldEpoch, 1, First, maxHp: 55);
        store.Register("int_land01", oldEpoch, 2, First, maxHp: 60);
        store.ApplyDamage(Key("int_land01", oldEpoch, 1, First), damage: 20);

        store.RemoveEpoch("int_land01", oldEpoch);

        Assert.False(store.TryGet(Key("int_land01", oldEpoch, 1, First), out _));
        Assert.False(store.TryGet(Key("int_land01", oldEpoch, 2, First), out _));
    }

    [Fact]
    public void RemoveEpoch_DoesNotAffectADifferentMapsEntriesUnderTheSameEpochValue()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("izlude", epoch, 1, First, maxHp: 55);
        store.Register("geffen", epoch, 1, First, maxHp: 55);

        store.RemoveEpoch("izlude", epoch);

        Assert.False(store.TryGet(Key("izlude", epoch, 1, First), out _));
        Assert.True(store.TryGet(Key("geffen", epoch, 1, First), out _)); // A different map's entry under the SAME epoch value is untouched.
    }

    [Fact]
    public void Remove_RemovesExactlyOneKey()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, 1, First, maxHp: 55);
        store.Register("int_land01", epoch, 2, First, maxHp: 55);

        store.Remove(Key("int_land01", epoch, 1, First));

        Assert.False(store.TryGet(Key("int_land01", epoch, 1, First), out _));
        Assert.True(store.TryGet(Key("int_land01", epoch, 2, First), out _));
    }

    // Concurrency: exactly-one-death guarantee under simultaneous lethal hits against the SAME key.
    [Fact]
    public async Task ConcurrentLethalHits_SameKey_ExactlyOneReportsTheDeathTransition_HpReachesZero_NoNegativeHp()
    {
        for (var iteration = 0; iteration < 20; iteration++) // Repeat to surface races.
        {
            var store = new MonsterCombatStateStore();
            var epoch = Epoch();
            var key = Key("int_land01", epoch, 1, First);
            store.Register("int_land01", epoch, 1, First, maxHp: 100);

            const int concurrentHits = 8;
            var barrier = new Barrier(concurrentHits);
            var tasks = Enumerable.Range(0, concurrentHits).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return store.ApplyDamage(key, damage: 100);
            })).ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(r => r.KilledByThisHit));
            Assert.True(store.TryGet(key, out var finalState));
            Assert.Equal(0u, finalState.CurrentHp);
            // No result observed a negative/underflowed HP value.
            Assert.All(results, r => Assert.True(r.HpAfter is 0 or 100));
        }
    }

    [Fact]
    public async Task ConcurrentNonLethalDamage_SameKey_NoLostUpdates()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var store = new MonsterCombatStateStore();
            var epoch = Epoch();
            var key = Key("int_land01", epoch, 1, First);
            store.Register("int_land01", epoch, 1, First, maxHp: 1000);

            const int concurrentHits = 20;
            const uint damagePerHit = 10;
            var barrier = new Barrier(concurrentHits);
            var tasks = Enumerable.Range(0, concurrentHits).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return store.ApplyDamage(key, damage: damagePerHit);
            })).ToArray();
            await Task.WhenAll(tasks);

            Assert.True(store.TryGet(key, out var finalState));
            Assert.Equal((uint)(1000 - concurrentHits * damagePerHit), finalState.CurrentHp);
        }
    }

    // ===== Item 2 of the Step 6 correctness-hardening pass: Peek/TryCommitDamage's own CAS-style
    // calculate -> confirm -> commit contract, used by MonsterCombatCoordinator.CalculateAttack/
    // CommitAttack(Async) to avoid mutating local HP before an external World confirmation lands. =====

    [Fact]
    public void Peek_DoesNotMutateAnything_ReportsCurrentHpForAliveLife()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 55);

        var peek = store.Peek(key);

        Assert.Equal(MonsterCombatDamageStatus.Applied, peek.Status);
        Assert.Equal(55u, peek.CurrentHp);
        Assert.True(store.TryGet(key, out var unchanged));
        Assert.Equal(55u, unchanged.CurrentHp); // Peek never mutates.
    }

    [Fact]
    public void Peek_StaleLife_ReportsStaleLife()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, 1, First, maxHp: 55);

        var peek = store.Peek(Key("int_land01", epoch, 1, First.Next()));

        Assert.Equal(MonsterCombatDamageStatus.StaleLife, peek.Status);
    }

    [Fact]
    public void Peek_AlreadyDead_ReportsAlreadyDead()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 1);
        store.ApplyDamage(key, damage: 1);

        var peek = store.Peek(key);

        Assert.Equal(MonsterCombatDamageStatus.AlreadyDead, peek.Status);
    }

    [Fact]
    public void TryCommitDamage_ExpectedHpMatches_AppliesExactlyLikeApplyDamage()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 55);
        var peek = store.Peek(key);

        var result = store.TryCommitDamage(key, peek.CurrentHp, damage: 10);

        Assert.Equal(MonsterCombatDamageStatus.Applied, result.Status);
        Assert.Equal(55u, result.HpBefore);
        Assert.Equal(45u, result.HpAfter);
        Assert.True(store.TryGet(key, out var state));
        Assert.Equal(45u, state.CurrentHp);
    }

    // The core scenario item 2 exists to prevent: a caller Peeks, awaits an external confirmation
    // (a World RPC, simulated here as a concurrent hit landing in the gap), then tries to commit
    // against the now-STALE pre-image it captured before the await - this must be rejected, not
    // silently applied on top of the concurrent hit's own result (which would double-count damage
    // or resurrect an already-dead life's HP arithmetic).
    [Fact]
    public void TryCommitDamage_ConcurrentHitChangedHpDuringTheGap_ReturnsConflict_AppliesNothing()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 55);
        var peek = store.Peek(key); // Caller A's own pre-image, captured BEFORE its own simulated "await".

        store.ApplyDamage(key, damage: 5); // A concurrent hit (Caller B) lands while A's confirmation is in flight.

        var result = store.TryCommitDamage(key, peek.CurrentHp, damage: 10); // A tries to commit against its now-stale pre-image.

        Assert.Equal(MonsterCombatDamageStatus.Conflict, result.Status);
        Assert.True(store.TryGet(key, out var state));
        Assert.Equal(50u, state.CurrentHp); // Only B's own 5 damage landed - A's commit applied NOTHING.
    }

    [Fact]
    public void TryCommitDamage_StaleLife_ReturnsStaleLife_AppliesNothing()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, 1, First, maxHp: 55);
        var staleKey = Key("int_land01", epoch, 1, First.Next());

        var result = store.TryCommitDamage(staleKey, expectedCurrentHp: 55, damage: 10);

        Assert.Equal(MonsterCombatDamageStatus.StaleLife, result.Status);
    }

    [Fact]
    public void TryCommitDamage_AlreadyDead_ReturnsAlreadyDead_AppliesNothing()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 1);
        store.ApplyDamage(key, damage: 1);

        var result = store.TryCommitDamage(key, expectedCurrentHp: 0, damage: 1);

        Assert.Equal(MonsterCombatDamageStatus.AlreadyDead, result.Status);
    }

    // Concurrency: several concurrent calculate->commit sequences against the same key must never
    // lose an update or double-apply - exactly like ApplyDamage's own established concurrency
    // guarantee, but exercised through the Peek/TryCommitDamage CAS pair specifically. A Conflict
    // result means that attempt applied nothing (by design) - this test only asserts the INVARIANT
    // that final HP correctly reflects however many attempts actually got Applied, never more and
    // never negative, not that every attempt necessarily succeeds (a caller retries on Conflict in
    // production; this test does not need to model that retry to prove the store's own atomicity).
    [Fact]
    public async Task ConcurrentCalculateThenCommit_SameKey_NeverLosesOrDoublesAnUpdate()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var store = new MonsterCombatStateStore();
            var epoch = Epoch();
            var key = Key("int_land01", epoch, 1, First);
            store.Register("int_land01", epoch, 1, First, maxHp: 1000);

            const int concurrentAttempts = 16;
            const uint damagePerHit = 10;
            var barrier = new Barrier(concurrentAttempts);
            var tasks = Enumerable.Range(0, concurrentAttempts).Select(_ => Task.Run(() =>
            {
                var peek = store.Peek(key);
                barrier.SignalAndWait(); // Maximize the window where a concurrent hit could land between Peek and commit.
                return store.TryCommitDamage(key, peek.CurrentHp, damagePerHit);
            })).ToArray();
            var results = await Task.WhenAll(tasks);

            var appliedCount = results.Count(r => r.Status == MonsterCombatDamageStatus.Applied);
            Assert.True(store.TryGet(key, out var finalState));
            Assert.Equal((uint)(1000 - appliedCount * damagePerHit), finalState.CurrentHp);
            Assert.True(appliedCount >= 1, "At least one of the concurrent attempts must have succeeded.");
        }
    }

    // ===== Item 1 of the Step 6 final correctness pass: CommitConfirmedDeath is the ONLY way a
    // lethal hit may transition local HP to 0, and it must ONLY ever be called AFTER the caller
    // already holds World's own MarkedDead confirmation for the EXACT life. =====

    [Fact]
    public void CommitConfirmedDeath_ExactLife_TransitionsToZero_ReportsKilledByThisHit()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 55);

        var result = store.CommitConfirmedDeath(key, damage: 55);

        Assert.Equal(MonsterCombatDamageStatus.Applied, result.Status);
        Assert.Equal(55u, result.HpBefore);
        Assert.Equal(0u, result.HpAfter);
        Assert.True(result.KilledByThisHit);
        Assert.True(store.TryGet(key, out var state));
        Assert.Equal(0u, state.CurrentHp);
    }

    [Fact]
    public void CommitConfirmedDeath_StaleLife_ReportsStaleLife_NeverCreatesAMissingLife()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, 1, First, maxHp: 55);
        var staleKey = Key("int_land01", epoch, 1, First.Next());

        var result = store.CommitConfirmedDeath(staleKey, damage: 55);

        Assert.Equal(MonsterCombatDamageStatus.StaleLife, result.Status);
        Assert.False(store.TryGet(staleKey, out _)); // Never speculatively created.
        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out var unchanged));
        Assert.Equal(55u, unchanged.CurrentHp); // The current life's own entry is untouched.
    }

    [Fact]
    public void CommitConfirmedDeath_AlreadyDead_ReportsAlreadyDead_NoMutation()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 1);
        store.CommitConfirmedDeath(key, damage: 1);

        var second = store.CommitConfirmedDeath(key, damage: 1);

        Assert.Equal(MonsterCombatDamageStatus.AlreadyDead, second.Status);
        Assert.False(second.KilledByThisHit);
        Assert.True(store.TryGet(key, out var state));
        Assert.Equal(0u, state.CurrentHp);
    }

    // The core race this operation exists to handle correctly: a candidate calculated lethal at
    // HP=N (the pre-image observed BEFORE the TryMarkMonsterDeadAsync RPC was sent) remains lethal
    // if a DIFFERENT valid local hit further lowers HP while that RPC is in flight - CommitConfirmedDeath
    // must use the ACTUAL HP present when it runs, never the earlier candidate's own stale pre-image,
    // for the final clamped HpBefore/HpAfter/damage outcome.
    [Fact]
    public void CommitConfirmedDeath_AnotherValidHitLoweredHpWhileConfirmationWasInFlight_UsesActualCurrentHpAtCommitTime()
    {
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 100);
        // Attacker A calculates a candidate lethal hit for damage=100 while CurrentHp was 100 (both
        // observed via the store's own Peek at that earlier moment - simulated here by simply NOT
        // yet committing). Meanwhile attacker B's own hit lands FIRST, lowering HP to 40 (still
        // alive) - representing "another valid local hit landed while A's own TryMarkMonsterDeadAsync
        // RPC was in flight".
        store.ApplyDamage(key, damage: 60); // B's hit: 100 -> 40, not lethal.
        Assert.True(store.TryGet(key, out var afterB));
        Assert.Equal(40u, afterB.CurrentHp);

        // A's own confirmed-death commit now runs, using its ORIGINAL damage=100 (calculated against
        // the stale HP=100 pre-image) - CommitConfirmedDeath must clamp against the ACTUAL current
        // HP (40), not silently underflow or ignore B's own already-applied damage.
        var result = store.CommitConfirmedDeath(key, damage: 100);

        Assert.Equal(MonsterCombatDamageStatus.Applied, result.Status);
        Assert.Equal(40u, result.HpBefore); // The ACTUAL HP present at commit time, not A's stale 100.
        Assert.Equal(0u, result.HpAfter);
        Assert.True(result.KilledByThisHit);
        Assert.True(store.TryGet(key, out var final));
        Assert.Equal(0u, final.CurrentHp);
    }

    [Fact]
    public void CommitConfirmedDeath_NeverAwaits_PurelySynchronous()
    {
        // Compile-time/contract proof: CommitConfirmedDeath's own signature is synchronous (no Task
        // return, no async keyword) - this test exists to document/pin that contract explicitly
        // rather than relying solely on reading the method signature.
        var store = new MonsterCombatStateStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First, maxHp: 10);

        MonsterCombatDamageResult result = store.CommitConfirmedDeath(key, damage: 10); // Would not compile as a synchronous assignment if this returned a Task.

        Assert.Equal(MonsterCombatDamageStatus.Applied, result.Status);
    }
}
