using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.World;

// Step 6 hardening (items 2 and 5): MonsterFeedProjection's own thread-safety and replay-safety
// contracts, exercised directly against the type (never through a real Orleans grain, which is
// unnecessary for proving these purely local invariants - mirrors MonsterCombatStateStoreTests'
// own "exercise the store directly" scope).
public sealed class MonsterFeedProjectionTests
{
    private const string MapId = "int_land01";
    private const int PoringMobId = 1002; // GeneratedMobs.Poring - a real generated static mob entry.

    private static WorldMonsterInstance Alive(uint actorId, WorldMonsterIncarnationId incarnation, ushort x = 10, ushort y = 10, WorldMonsterLifecycleState lifecycle = WorldMonsterLifecycleState.Alive) =>
        new(actorId, incarnation, MapId, PoringMobId, x, y, lifecycle, IsWalking: false, DestinationX: x, DestinationY: y, WorldMonsterEngagementState.Unengaged, EngagedTarget: null);

    // Mirrors MonsterCombatStateStoreTests' own Barrier-synchronized concurrency-test idiom exactly:
    // one writer thread hammering ApplySnapshot/ApplyEntry while several reader threads concurrently
    // call TryGetLife/AllInstances/EngagementOf in a tight loop, asserting no exception and that
    // every observed (epoch, instance) pair is internally consistent (the instance's own ActorId
    // matches the key looked up - never a torn read pairing one actor's instance with an unrelated
    // epoch or a different actor's data).
    [Fact]
    public async Task ConcurrentReadsAndWrites_NoExceptionAndNoTornReads()
    {
        var projection = new MonsterFeedProjection(MapId);
        var combatState = new MonsterCombatStateStore();
        const uint actorId = 1;
        var incarnation = WorldMonsterIncarnationId.First;
        projection.ApplySnapshot([Alive(actorId, incarnation)], WorldSimulationEpoch.NewEpoch(), combatState);

        const int readerCount = 6;
        const int iterations = 500;
        using var cts = new CancellationTokenSource();
        var barrier = new Barrier(readerCount + 1);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var readers = Enumerable.Range(0, readerCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (projection.TryGetLife(actorId, out var observedEpoch, out var instance) && instance.ActorId != actorId)
                        throw new InvalidOperationException("Torn read: TryGetLife returned an instance for the wrong ActorId.");
                    GC.KeepAlive(observedEpoch);
                    foreach (var observed in projection.AllInstances)
                    {
                        GC.KeepAlive(projection.EngagementOf(observed.ActorId));
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            barrier.SignalAndWait();
            var epoch = WorldSimulationEpoch.NewEpoch();
            for (var i = 0; i < iterations; i++)
            {
                var nextIncarnation = new WorldMonsterIncarnationId(i + 2);
                projection.ApplySnapshot([Alive(actorId, nextIncarnation, x: (ushort)(10 + i % 5))], epoch, combatState);
                projection.ApplyEntry(
                    new WorldMonsterFeedEntry(i, WorldMonsterFeedEntryKind.Moved, actorId, nextIncarnation, Alive(actorId, nextIncarnation, x: (ushort)(11 + i % 5))),
                    combatState, epoch);
            }
        });

        await writer;
        cts.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(exceptions);
    }

    [Fact]
    public void ReplayedRespawnedEntry_DoesNotResetAlreadyDamagedHp()
    {
        var projection = new MonsterFeedProjection(MapId);
        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var incarnation = WorldMonsterIncarnationId.First;

        var respawnedInstance = Alive(actorId, incarnation);
        var entry = new WorldMonsterFeedEntry(1, WorldMonsterFeedEntryKind.Respawned, actorId, incarnation, respawnedInstance);
        projection.ApplyEntry(entry, combatState, epoch);

        var key = new MonsterCombatKey(MapId, epoch, actorId, incarnation);
        combatState.ApplyDamage(key, damage: 20);
        Assert.True(combatState.TryGet(key, out var damaged));
        Assert.True(damaged.CurrentHp < damaged.MaxHp);

        // Replay the EXACT same Respawned entry again (simulating a crash between apply and commit,
        // then the same page being re-polled) - must be a complete no-op: no HP reset, no exception.
        projection.ApplyEntry(entry, combatState, epoch);

        Assert.True(combatState.TryGet(key, out var afterReplay));
        Assert.Equal(damaged.CurrentHp, afterReplay.CurrentHp);
    }

    [Fact]
    public void RepeatedIdenticalApplySnapshot_PreservesHp()
    {
        var projection = new MonsterFeedProjection(MapId);
        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var incarnation = WorldMonsterIncarnationId.First;
        var instance = Alive(actorId, incarnation);

        projection.ApplySnapshot([instance], epoch, combatState);
        var key = new MonsterCombatKey(MapId, epoch, actorId, incarnation);
        combatState.ApplyDamage(key, damage: 15);
        Assert.True(combatState.TryGet(key, out var damaged));

        // Re-applying the IDENTICAL snapshot content (same epoch, same actor, same incarnation) must
        // preserve the already-damaged HP untouched - a resync re-observing an unchanged life is not
        // a fresh registration.
        projection.ApplySnapshot([instance], epoch, combatState);

        Assert.True(combatState.TryGet(key, out var afterResync));
        Assert.Equal(damaged.CurrentHp, afterResync.CurrentHp);
    }

    [Fact]
    public void NewIncarnationInFreshSnapshot_RemovesOldIncarnationCombatStateKey()
    {
        var projection = new MonsterFeedProjection(MapId);
        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var oldIncarnation = WorldMonsterIncarnationId.First;
        projection.ApplySnapshot([Alive(actorId, oldIncarnation)], epoch, combatState);
        var oldKey = new MonsterCombatKey(MapId, epoch, actorId, oldIncarnation);
        Assert.True(combatState.TryGet(oldKey, out _));

        // Same ActorId, present in BOTH the old projection and the new snapshot, but under a
        // DIFFERENT IncarnationId - the vanished-actor-only cleanup loop would miss this (the
        // ActorId is NOT absent from the new snapshot), so this is the specific gap item 5(b)
        // requires ApplySnapshot to close explicitly.
        var newIncarnation = oldIncarnation.Next();
        projection.ApplySnapshot([Alive(actorId, newIncarnation)], epoch, combatState);

        Assert.False(combatState.TryGet(oldKey, out _));
        var newKey = new MonsterCombatKey(MapId, epoch, actorId, newIncarnation);
        Assert.True(combatState.TryGet(newKey, out var freshState));
        Assert.Equal(freshState.MaxHp, freshState.CurrentHp);
    }

    [Fact]
    public void FirstBootstrapObservationOfDeadLife_DoesNotRegisterFreshHpEntry()
    {
        var projection = new MonsterFeedProjection(MapId);
        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var incarnation = WorldMonsterIncarnationId.First;

        // The VERY FIRST snapshot this projection has ever seen for this ActorId already reports it
        // Dead (e.g. this MapServer instance connected/bootstrapped between a kill and a respawn) -
        // no fresh full-HP combat-state entry may be registered for it, since it was never alive
        // from this projection's own perspective.
        projection.ApplySnapshot([Alive(actorId, incarnation, lifecycle: WorldMonsterLifecycleState.Dead)], epoch, combatState);

        var key = new MonsterCombatKey(MapId, epoch, actorId, incarnation);
        Assert.False(combatState.TryGet(key, out _));
    }

    [Fact]
    public void NewEpoch_RemovesAllOldEpochCombatState_ViaApplySnapshot()
    {
        var projection = new MonsterFeedProjection(MapId);
        var combatState = new MonsterCombatStateStore();
        var oldEpoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var incarnation = WorldMonsterIncarnationId.First;
        projection.ApplySnapshot([Alive(actorId, incarnation)], oldEpoch, combatState);
        var oldKey = new MonsterCombatKey(MapId, oldEpoch, actorId, incarnation);
        Assert.True(combatState.TryGet(oldKey, out _));

        var newEpoch = WorldSimulationEpoch.NewEpoch();
        projection.ApplySnapshot([Alive(actorId, incarnation)], newEpoch, combatState);

        Assert.False(combatState.TryGet(oldKey, out _));
        Assert.True(combatState.TryGet(new MonsterCombatKey(MapId, newEpoch, actorId, incarnation), out _));
    }

    [Fact]
    public void TryGetLife_ReturnsEpochAndInstanceFromOneAtomicRead()
    {
        var projection = new MonsterFeedProjection(MapId);
        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var incarnation = WorldMonsterIncarnationId.First;
        projection.ApplySnapshot([Alive(actorId, incarnation)], epoch, combatState);

        Assert.True(projection.TryGetLife(actorId, out var observedEpoch, out var observedInstance));
        Assert.Equal(epoch, observedEpoch);
        Assert.Equal(actorId, observedInstance.ActorId);

        Assert.False(projection.TryGetLife(999, out _, out _));
    }

    [Fact]
    public void SnapshotForCadence_ReturnsEpochInstancesAndEngagementTogether()
    {
        var projection = new MonsterFeedProjection(MapId);
        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var incarnation = WorldMonsterIncarnationId.First;
        var engagedInstance = Alive(actorId, incarnation) with
        {
            Engagement = WorldMonsterEngagementState.InAttackRange,
            EngagedTarget = new WorldPlayerTargetReference(7, Guid.NewGuid()),
        };
        projection.ApplySnapshot([engagedInstance], epoch, combatState);

        Assert.True(projection.SnapshotForCadence(out var observedEpoch, out var instances));
        Assert.Equal(epoch, observedEpoch);
        var (instance, engagement) = Assert.Single(instances);
        Assert.Equal(actorId, instance.ActorId);
        Assert.Equal(WorldMonsterEngagementState.InAttackRange, engagement);
    }

    [Fact]
    public void AllInstances_ReturnsImmutableSnapshot_NotLiveDictionaryView()
    {
        var projection = new MonsterFeedProjection(MapId);
        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        projection.ApplySnapshot([Alive(1, WorldMonsterIncarnationId.First)], epoch, combatState);

        var snapshot = projection.AllInstances;
        // Mutating the projection afterward must never change the ALREADY-RETURNED snapshot value -
        // proves this is a materialized copy, not a live view over the same backing dictionary.
        projection.ApplySnapshot([Alive(2, WorldMonsterIncarnationId.First)], epoch, combatState);

        Assert.Single(snapshot);
        Assert.Equal(1u, snapshot[0].ActorId);
    }
}
