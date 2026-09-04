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
}
