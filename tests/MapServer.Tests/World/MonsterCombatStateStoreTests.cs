using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Step 5: MonsterCombatStateStore is the actual owner of CurrentHp/NextAttackAt on the migrated
// combat path - these tests exercise the store directly (not through MonsterCombatCoordinator),
// proving its own key isolation, atomicity, and lifecycle-transition contracts hold independent of
// any caller.
public sealed class MonsterCombatStateStoreTests
{
    private static MobDefinition MakeMob(uint maxHp = 55) => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: maxHp,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872, AttackMotion: 672, DamageMotion: 480,
        BaseExp: 0, JobExp: 0, Mode: MobMode.CanMove,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static MobSpawnDefinition MakeSpawn(string map = "int_land01", uint maxHp = 55) =>
        new(MakeMob(maxHp), map, 40, 5000, 0, new("rAthena", "abc", "npc/re/mobs/int_land.txt", 12));

    private static MobInstance MakeInstance(string map = "int_land01", uint actorId = 1, uint maxHp = 55) =>
        new(actorId, MakeSpawn(map, maxHp), 0, 0);

    [Fact]
    public void CombatState_IsIsolatedByMapId_SameActorIdDifferentMap_AreIndependentEntries()
    {
        var store = new MonsterCombatStateStore();
        var izlude = MakeInstance("izlude", actorId: 1);
        var geffen = MakeInstance("geffen", actorId: 1); // Same ActorId, different map - a real scenario is unlikely but the key must still isolate it.
        store.Register("izlude", izlude);
        store.Register("geffen", geffen);

        store.ApplyDamage("izlude", izlude, izlude.IncarnationId, damage: 10);

        Assert.True(store.TryGet("izlude", izlude, out var izludeState));
        Assert.Equal(45u, izludeState.CurrentHp);
        Assert.True(store.TryGet("geffen", geffen, out var geffenState));
        Assert.Equal(55u, geffenState.CurrentHp); // Untouched - damage to the "izlude" key must never leak to "geffen".
    }

    [Fact]
    public void CombatState_IsIsolatedByActorId_SameMapDifferentActorId_AreIndependentEntries()
    {
        var store = new MonsterCombatStateStore();
        var a = MakeInstance("int_land01", actorId: 1);
        var b = MakeInstance("int_land01", actorId: 2);
        store.Register("int_land01", a);
        store.Register("int_land01", b);

        store.ApplyDamage("int_land01", a, a.IncarnationId, damage: 20);

        Assert.True(store.TryGet("int_land01", a, out var stateA));
        Assert.Equal(35u, stateA.CurrentHp);
        Assert.True(store.TryGet("int_land01", b, out var stateB));
        Assert.Equal(55u, stateB.CurrentHp);
    }

    [Fact]
    public void CombatState_IsIsolatedByIncarnationId_RespawnCreatesAnIndependentEntry()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);
        store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 55); // Kill it.
        Assert.False(instance.IsAlive);

        var oldIncarnation = instance.IncarnationId;
        instance.TryScheduleRespawn(1000);
        Assert.True(instance.TryRespawn(1000, () => (true, new MobPosition(5, 5))));
        Assert.NotEqual(oldIncarnation, instance.IncarnationId);

        // The OLD incarnation's entry (if it still exists) must remain at 0, and the store must
        // report the NEW incarnation as having fresh, independent state once registered.
        store.Register(instance.Map, instance);
        Assert.True(store.TryGet(instance.Map, instance.ActorId, instance.IncarnationId, out var freshState));
        Assert.Equal(55u, freshState.CurrentHp);
        Assert.Null(freshState.NextAttackAt);
    }

    [Fact]
    public void StaleIncarnation_CannotReadCurrentCombatState()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);
        var staleIncarnation = instance.IncarnationId.Next(); // Not actually registered.

        Assert.False(store.TryGet(instance.Map, instance.ActorId, staleIncarnation, out _));
    }

    [Fact]
    public void StaleIncarnation_CannotMutateCombatState_ApplyDamageReturnsStaleIncarnation()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);
        var staleIncarnation = instance.IncarnationId.Next();

        var result = store.ApplyDamage(instance.Map, instance, staleIncarnation, damage: 10);

        Assert.Equal(MonsterCombatDamageStatus.StaleIncarnation, result.Status);
        Assert.True(store.TryGet(instance.Map, instance, out var unchanged));
        Assert.Equal(55u, unchanged.CurrentHp); // Untouched by the rejected stale-incarnation call.
    }

    [Fact]
    public void StaleIncarnation_CannotScheduleNextAttack_SilentlyIgnored()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);
        var staleIncarnation = instance.IncarnationId.Next();
        var dueAt = DateTimeOffset.UnixEpoch.AddSeconds(5);

        store.ScheduleNextAttack(instance.Map, instance.ActorId, staleIncarnation, dueAt);

        Assert.True(store.TryGet(instance.Map, instance, out var current));
        Assert.Null(current.NextAttackAt); // The stale-incarnation schedule must not have landed on the current entry.
    }

    [Fact]
    public void ApplyDamage_MutatesExactlyOneCurrentHpSourceOfTruth()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);

        store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 10);

        Assert.True(store.TryGet(instance.Map, instance, out var state));
        Assert.Equal(45u, state.CurrentHp);
        // MobInstance's OWN CurrentHp (superseded on this path) must NOT have been touched by the
        // store's damage application - there is exactly one mutable HP source of truth reachable
        // from this call, and it is the store's own entry, never MobInstance's internal field.
        Assert.Equal(55u, instance.CurrentHp);
    }

    [Fact]
    public void LethalDamage_ReachesZeroExactlyOnce_AndMarksMobInstanceDead()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);

        var result = store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 55);

        Assert.Equal(MonsterCombatDamageStatus.Applied, result.Status);
        Assert.Equal(0u, result.HpAfter);
        Assert.True(result.KilledByThisHit);
        Assert.False(instance.IsAlive); // MarkDeadIfNeeded's own lifecycle transition took effect.

        // A second hit against the same (now-dead) key must not report a second kill or go negative.
        var second = store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 10);
        Assert.Equal(MonsterCombatDamageStatus.AlreadyDead, second.Status);
        Assert.False(second.KilledByThisHit);
        Assert.True(store.TryGet(instance.Map, instance, out var finalState));
        Assert.Equal(0u, finalState.CurrentHp);
    }

    [Fact]
    public void DamageAfterDeath_CannotProduceAnotherLethalTransition()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);
        store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 55);
        Assert.False(instance.IsAlive);

        var result = store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 1);

        Assert.Equal(MonsterCombatDamageStatus.AlreadyDead, result.Status);
        Assert.False(result.KilledByThisHit);
    }

    [Fact]
    public void NonLethalDamage_ClampsToZero_NeverUnderflows()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);

        var result = store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 9999);

        Assert.Equal(0u, result.HpAfter);
        Assert.True(result.KilledByThisHit);
    }

    [Fact]
    public void NextAttackAt_IsStoredAndUpdatedOnlyInTheCombatStateStore()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);
        var dueAt = DateTimeOffset.UnixEpoch.AddMilliseconds(1872);

        store.ScheduleNextAttack(instance.Map, instance.ActorId, instance.IncarnationId, dueAt);

        Assert.True(store.TryGet(instance.Map, instance, out var state));
        Assert.Equal(dueAt, state.NextAttackAt);
        // MobInstance's own (superseded) NextAttackAt must remain untouched - the store never
        // mirrors/synchronizes its own writes back into MobInstance.
        Assert.Null(instance.NextAttackAt);
    }

    [Fact]
    public void Respawn_NewIncarnation_ReceivesFreshFullHpAndFreshCadenceState()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance(maxHp: 55);
        store.Register(instance.Map, instance);
        store.ScheduleNextAttack(instance.Map, instance.ActorId, instance.IncarnationId, DateTimeOffset.UnixEpoch.AddSeconds(2));
        store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 55);
        Assert.False(instance.IsAlive);

        instance.TryScheduleRespawn(1000);
        Assert.True(instance.TryRespawn(1000, () => (true, new MobPosition(10, 10))));
        store.Register(instance.Map, instance); // Fresh registration for the new incarnation (mirrors MapTcpServer's own respawn hook).

        Assert.True(store.TryGet(instance.Map, instance, out var freshState));
        Assert.Equal(55u, freshState.CurrentHp);
        Assert.Equal(55u, freshState.MaxHp);
        Assert.Null(freshState.NextAttackAt);
    }

    [Fact]
    public void MovementPositionChanges_DoNotMutateCombatState()
    {
        var store = new MonsterCombatStateStore();
        var instance = MakeInstance();
        store.Register(instance.Map, instance);
        store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 15);

        // Movement/position mutation via MobInstance's own IMonsterActorView-shaped API.
        instance.TryStartChase([(0, 0), (1, 0)], orthogonalStepMs: 150, DateTimeOffset.UnixEpoch);
        instance.AdvanceMovement(DateTimeOffset.UnixEpoch.AddMilliseconds(150));

        Assert.True(store.TryGet(instance.Map, instance, out var state));
        Assert.Equal(40u, state.CurrentHp); // Unaffected by movement.
    }

    // Concurrency: exactly-one-death guarantee under simultaneous lethal hits against the SAME key.
    [Fact]
    public async Task ConcurrentLethalHits_SameKey_ExactlyOneReportsTheDeathTransition_HpReachesZero_NoNegativeHp()
    {
        for (var iteration = 0; iteration < 20; iteration++) // Repeat to surface races.
        {
            var store = new MonsterCombatStateStore();
            var instance = MakeInstance(maxHp: 100);
            store.Register(instance.Map, instance);

            const int concurrentHits = 8;
            var barrier = new Barrier(concurrentHits);
            var tasks = Enumerable.Range(0, concurrentHits).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: 100);
            })).ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(r => r.KilledByThisHit));
            Assert.True(store.TryGet(instance.Map, instance, out var finalState));
            Assert.Equal(0u, finalState.CurrentHp);
            Assert.False(instance.IsAlive);
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
            var instance = MakeInstance(maxHp: 1000);
            store.Register(instance.Map, instance);

            const int concurrentHits = 20;
            const uint damagePerHit = 10;
            var barrier = new Barrier(concurrentHits);
            var tasks = Enumerable.Range(0, concurrentHits).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return store.ApplyDamage(instance.Map, instance, instance.IncarnationId, damage: damagePerHit);
            })).ToArray();
            await Task.WhenAll(tasks);

            Assert.True(store.TryGet(instance.Map, instance, out var finalState));
            Assert.Equal((uint)(1000 - concurrentHits * damagePerHit), finalState.CurrentHp);
        }
    }
}
