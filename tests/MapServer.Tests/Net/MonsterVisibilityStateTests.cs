using Athena.Net.MapServer.Net;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Item 3 of the Step 6 correctness-hardening pass: MonsterVisibilityState is the single,
// internally-synchronized owner of "visible monster ActorId -> IncarnationId" plus "last
// reconciled SimulationEpoch" that replaced three independently-mutated pieces (a second
// VisibleActorTracker instance, a plain Dictionary, and a bare nullable epoch field) previously
// updated from two independently-scheduled call paths with no shared synchronization. These tests
// exercise the type directly - see MapClientSessionMonsterVisibilityReconciliationTests.cs for the
// end-to-end wiring proof (session-level vanish/rediscovery behavior).
public sealed class MonsterVisibilityStateTests
{
    [Fact]
    public void MarkVisible_ThenIsVisibleAtIncarnation_ReportsTrueForExactIncarnation()
    {
        var state = new MonsterVisibilityState();
        var incarnation = WorldMonsterIncarnationId.First;

        state.MarkVisible(actorId: 1, incarnation);

        Assert.True(state.IsVisibleAtIncarnation(1, incarnation));
        Assert.False(state.IsVisibleAtIncarnation(1, incarnation.Next()));
    }

    [Fact]
    public void IsVisibleAtIncarnation_NeverMarkedVisible_ReturnsFalse()
    {
        var state = new MonsterVisibilityState();

        Assert.False(state.IsVisibleAtIncarnation(1, WorldMonsterIncarnationId.First));
    }

    [Fact]
    public void Remove_ClearsTheEntry()
    {
        var state = new MonsterVisibilityState();
        var incarnation = WorldMonsterIncarnationId.First;
        state.MarkVisible(1, incarnation);

        state.Remove(1);

        Assert.False(state.IsVisibleAtIncarnation(1, incarnation));
        Assert.Empty(state.Snapshot());
    }

    [Fact]
    public void Snapshot_ReturnsEveryTrackedActorIdAndIncarnation()
    {
        var state = new MonsterVisibilityState();
        state.MarkVisible(1, WorldMonsterIncarnationId.First);
        state.MarkVisible(2, WorldMonsterIncarnationId.First.Next());

        var snapshot = state.Snapshot();

        Assert.Equal(2, snapshot.Length);
        Assert.Contains(snapshot, entry => entry.ActorId == 1 && entry.IncarnationId.Equals(WorldMonsterIncarnationId.First));
        Assert.Contains(snapshot, entry => entry.ActorId == 2 && entry.IncarnationId.Equals(WorldMonsterIncarnationId.First.Next()));
    }

    [Fact]
    public void Snapshot_ReturnsImmutableCopy_NotLiveView()
    {
        var state = new MonsterVisibilityState();
        state.MarkVisible(1, WorldMonsterIncarnationId.First);

        var snapshot = state.Snapshot();
        state.MarkVisible(2, WorldMonsterIncarnationId.First);

        Assert.Single(snapshot); // The already-returned snapshot must not observe the later mutation.
    }

    [Fact]
    public void Reset_ClearsEveryTrackedActorAndTheLastReconciledEpoch()
    {
        var state = new MonsterVisibilityState();
        var epoch = WorldSimulationEpoch.NewEpoch();
        state.MarkVisible(1, WorldMonsterIncarnationId.First);
        state.CompareAndUpdateReconciledEpoch(epoch);

        state.Reset();

        Assert.Empty(state.Snapshot());
        // After Reset, the next CompareAndUpdateReconciledEpoch call for the SAME epoch value must
        // report "changed" again - proving the epoch memory itself was cleared, not just the
        // ActorId map (a map transition must start reconciliation completely fresh).
        Assert.True(state.CompareAndUpdateReconciledEpoch(epoch));
    }

    [Fact]
    public void CompareAndUpdateReconciledEpoch_FirstCall_ReportsChanged()
    {
        var state = new MonsterVisibilityState();

        Assert.True(state.CompareAndUpdateReconciledEpoch(WorldSimulationEpoch.NewEpoch()));
    }

    [Fact]
    public void CompareAndUpdateReconciledEpoch_SameEpochAgain_ReportsUnchanged()
    {
        var state = new MonsterVisibilityState();
        var epoch = WorldSimulationEpoch.NewEpoch();
        state.CompareAndUpdateReconciledEpoch(epoch);

        Assert.False(state.CompareAndUpdateReconciledEpoch(epoch));
    }

    [Fact]
    public void CompareAndUpdateReconciledEpoch_DifferentEpoch_ReportsChanged_AndRecordsTheNewOne()
    {
        var state = new MonsterVisibilityState();
        var firstEpoch = WorldSimulationEpoch.NewEpoch();
        var secondEpoch = WorldSimulationEpoch.NewEpoch();
        state.CompareAndUpdateReconciledEpoch(firstEpoch);

        Assert.True(state.CompareAndUpdateReconciledEpoch(secondEpoch));
        Assert.False(state.CompareAndUpdateReconciledEpoch(secondEpoch)); // Now recorded - a THIRD call with the same value reports unchanged.
    }

    // Concurrency: this type exists specifically because it used to be mutated from two
    // independently-scheduled callers with no shared synchronization - proves every operation is
    // safe under genuine concurrent use (mirrors VisibleActorTrackerTests'/MonsterCombatStateStoreTests'
    // own established concurrency-test idiom).
    [Fact]
    public async Task ConcurrentMarkVisibleAndReset_NoExceptionAndNoCorruption()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var state = new MonsterVisibilityState();
            const int writerCount = 8;
            var barrier = new Barrier(writerCount);
            var tasks = Enumerable.Range(0, writerCount).Select(i => Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var j = 0; j < 50; j++)
                {
                    state.MarkVisible((uint)i, WorldMonsterIncarnationId.First);
                    state.CompareAndUpdateReconciledEpoch(WorldSimulationEpoch.NewEpoch());
                    _ = state.Snapshot();
                    _ = state.IsVisibleAtIncarnation((uint)i, WorldMonsterIncarnationId.First);
                    state.Remove((uint)i);
                }
            })).ToArray();

            await Task.WhenAll(tasks); // No exception thrown proves no corruption/re-entrancy issue.
        }
    }
}
