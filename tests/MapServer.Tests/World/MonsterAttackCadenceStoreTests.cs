using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.World;

// Step 7 substep 9: MonsterAttackCadenceStore is keyed by the REAL World-issued (MapId,
// SimulationEpoch, ActorId, IncarnationId) tuple - these tests exercise the store directly (never
// through MonsterCombatCoordinator), proving its own key isolation and atomicity contracts hold
// independent of any caller. Post-cutover, the store owns NextAttackAt cadence bookkeeping ONLY -
// World's ApplyMonsterDamageAsync is the sole authority for CurrentHp/MaxHp/the Alive->Dead
// transition, so this store has no HP-mutating API surface at all any more (ApplyDamage/
// TryCommitDamage/CommitConfirmedDeath/Peek are gone, per the plan's §5 narrowing).
public sealed class MonsterAttackCadenceStoreTests
{
    private static WorldSimulationEpoch Epoch() => WorldSimulationEpoch.NewEpoch();
    private static WorldMonsterIncarnationId First => WorldMonsterIncarnationId.First;

    private static MonsterCombatKey Key(string mapId, WorldSimulationEpoch epoch, uint actorId, WorldMonsterIncarnationId incarnationId) =>
        new(mapId, epoch, actorId, incarnationId);

    [Fact]
    public void CadenceState_IsIsolatedByMapId_SameActorIdDifferentEpoch_AreIndependentEntries()
    {
        var store = new MonsterAttackCadenceStore();
        var izludeEpoch = Epoch();
        var geffenEpoch = Epoch();
        store.Register("izlude", izludeEpoch, actorId: 1, First);
        store.Register("geffen", geffenEpoch, actorId: 1, First); // Same ActorId, different map/epoch - the key must still isolate it.

        var dueAt = DateTimeOffset.UnixEpoch.AddSeconds(5);
        store.ScheduleNextAttack(Key("izlude", izludeEpoch, 1, First), dueAt);

        Assert.True(store.TryGet(Key("izlude", izludeEpoch, 1, First), out var izludeState));
        Assert.Equal(dueAt, izludeState.NextAttackAt);
        Assert.True(store.TryGet(Key("geffen", geffenEpoch, 1, First), out var geffenState));
        Assert.Null(geffenState.NextAttackAt); // Untouched - the schedule for the "izlude" key must never leak to "geffen".
    }

    [Fact]
    public void CadenceState_IsIsolatedByActorId_SameMapEpochDifferentActorId_AreIndependentEntries()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, actorId: 1, First);
        store.Register("int_land01", epoch, actorId: 2, First);

        var dueAt = DateTimeOffset.UnixEpoch.AddSeconds(2);
        store.ScheduleNextAttack(Key("int_land01", epoch, 1, First), dueAt);

        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out var stateA));
        Assert.Equal(dueAt, stateA.NextAttackAt);
        Assert.True(store.TryGet(Key("int_land01", epoch, 2, First), out var stateB));
        Assert.Null(stateB.NextAttackAt);
    }

    [Fact]
    public void CadenceState_IsIsolatedByIncarnationId_NewIncarnationIsAnIndependentEntry()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, actorId: 1, First);
        var dueAt = DateTimeOffset.UnixEpoch.AddSeconds(2);
        store.ScheduleNextAttack(Key("int_land01", epoch, 1, First), dueAt);

        var newIncarnation = First.Next();
        store.Register("int_land01", epoch, actorId: 1, newIncarnation); // Respawn re-registration under the new incarnation.

        Assert.True(store.TryGet(Key("int_land01", epoch, 1, newIncarnation), out var freshState));
        Assert.Null(freshState.NextAttackAt);
        // The OLD incarnation's own key is a SEPARATE entry - unaffected by the new registration.
        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out var oldState));
        Assert.Equal(dueAt, oldState.NextAttackAt);
    }

    [Fact]
    public void UnregisteredKey_CannotBeRead()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, actorId: 1, First);

        Assert.False(store.TryGet(Key("int_land01", epoch, actorId: 1, First.Next()), out _)); // Different (never-registered) incarnation.
        Assert.False(store.TryGet(Key("int_land01", Epoch(), actorId: 1, First), out _)); // Different (never-registered) epoch.
    }

    [Fact]
    public void StaleLife_CannotScheduleNextAttack_SilentlyIgnored()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, actorId: 1, First);
        var staleKey = Key("int_land01", epoch, actorId: 1, First.Next());
        var dueAt = DateTimeOffset.UnixEpoch.AddSeconds(5);

        store.ScheduleNextAttack(staleKey, dueAt);

        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out var current));
        Assert.Null(current.NextAttackAt); // The stale-life schedule must not have landed on the current entry.
    }

    [Fact]
    public void NextAttackAt_IsStoredAndUpdatedOnlyInTheCadenceStore()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First);
        var dueAt = DateTimeOffset.UnixEpoch.AddMilliseconds(1872);

        store.ScheduleNextAttack(key, dueAt);

        Assert.True(store.TryGet(key, out var state));
        Assert.Equal(dueAt, state.NextAttackAt);
    }

    [Fact]
    public void ScheduleNextAttack_OverwritesAPreviouslyScheduledValue()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First);
        store.ScheduleNextAttack(key, DateTimeOffset.UnixEpoch.AddSeconds(1));

        var newDueAt = DateTimeOffset.UnixEpoch.AddSeconds(9);
        store.ScheduleNextAttack(key, newDueAt);

        Assert.True(store.TryGet(key, out var state));
        Assert.Equal(newDueAt, state.NextAttackAt);
    }

    [Fact]
    public void NewIncarnation_ReceivesFreshCadenceState()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, 1, First);
        store.ScheduleNextAttack(Key("int_land01", epoch, 1, First), DateTimeOffset.UnixEpoch.AddSeconds(2));

        var newIncarnation = First.Next();
        store.Register("int_land01", epoch, 1, newIncarnation); // Fresh registration for the new incarnation (mirrors MapTcpServer's own Respawned feed-entry handling).

        Assert.True(store.TryGet(Key("int_land01", epoch, 1, newIncarnation), out var freshState));
        Assert.Null(freshState.NextAttackAt);
    }

    [Fact]
    public void Register_SameKeyAgain_ResetsNextAttackAtToNull()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        var key = Key("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 1, First);
        store.ScheduleNextAttack(key, DateTimeOffset.UnixEpoch.AddSeconds(2));

        store.Register("int_land01", epoch, 1, First); // Re-register the SAME key.

        Assert.True(store.TryGet(key, out var state));
        Assert.Null(state.NextAttackAt);
    }

    [Fact]
    public void NewEpoch_DiscardsAllOldEpochCadenceStateForThatMap()
    {
        var store = new MonsterAttackCadenceStore();
        var oldEpoch = Epoch();
        store.Register("int_land01", oldEpoch, 1, First);
        store.Register("int_land01", oldEpoch, 2, First);

        store.RemoveEpoch("int_land01", oldEpoch);

        Assert.False(store.TryGet(Key("int_land01", oldEpoch, 1, First), out _));
        Assert.False(store.TryGet(Key("int_land01", oldEpoch, 2, First), out _));
    }

    [Fact]
    public void RemoveEpoch_DoesNotAffectADifferentMapsEntriesUnderTheSameEpochValue()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        store.Register("izlude", epoch, 1, First);
        store.Register("geffen", epoch, 1, First);

        store.RemoveEpoch("izlude", epoch);

        Assert.False(store.TryGet(Key("izlude", epoch, 1, First), out _));
        Assert.True(store.TryGet(Key("geffen", epoch, 1, First), out _)); // A different map's entry under the SAME epoch value is untouched.
    }

    [Fact]
    public void Remove_RemovesExactlyOneKey()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, 1, First);
        store.Register("int_land01", epoch, 2, First);

        store.Remove(Key("int_land01", epoch, 1, First));

        Assert.False(store.TryGet(Key("int_land01", epoch, 1, First), out _));
        Assert.True(store.TryGet(Key("int_land01", epoch, 2, First), out _));
    }

    [Fact]
    public void Remove_UnregisteredKey_SilentlyIgnored()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, 1, First);

        store.Remove(Key("int_land01", epoch, 99, First)); // Never registered.

        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out _)); // Untouched.
    }

    [Fact]
    public void TryGet_ByLifeReference_MatchesTryGetByKey()
    {
        var store = new MonsterAttackCadenceStore();
        var epoch = Epoch();
        store.Register("int_land01", epoch, 1, First);
        var dueAt = DateTimeOffset.UnixEpoch.AddSeconds(3);
        store.ScheduleNextAttack(Key("int_land01", epoch, 1, First), dueAt);

        var life = new WorldMonsterLifeReference("int_land01", epoch, 1, First);
        Assert.True(store.TryGet(life, out var byLife));
        Assert.True(store.TryGet(Key("int_land01", epoch, 1, First), out var byKey));
        Assert.Equal(byKey, byLife);
        Assert.Equal(dueAt, byLife.NextAttackAt);
    }

    // Concurrency: several concurrent ScheduleNextAttack calls against the SAME key must never
    // corrupt the store's own dictionary (no torn/lost writes) - the store's single dictionary-wide
    // lock is the serialization point.
    [Fact]
    public async Task ConcurrentScheduleNextAttack_SameKey_NeverCorruptsTheStore()
    {
        for (var iteration = 0; iteration < 20; iteration++) // Repeat to surface races.
        {
            var store = new MonsterAttackCadenceStore();
            var epoch = Epoch();
            var key = Key("int_land01", epoch, 1, First);
            store.Register("int_land01", epoch, 1, First);

            const int concurrentWrites = 16;
            var barrier = new Barrier(concurrentWrites);
            var tasks = Enumerable.Range(0, concurrentWrites).Select(i => Task.Run(() =>
            {
                barrier.SignalAndWait();
                store.ScheduleNextAttack(key, DateTimeOffset.UnixEpoch.AddSeconds(i));
            })).ToArray();
            await Task.WhenAll(tasks);

            // Every concurrent write targeted the SAME key - the entry must still be readable and
            // hold exactly one of the written values (no corruption, no lost entry).
            Assert.True(store.TryGet(key, out var finalState));
            Assert.NotNull(finalState.NextAttackAt);
        }
    }
}
