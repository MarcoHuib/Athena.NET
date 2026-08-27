using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Direct, deterministic concurrency tests for VisibleActorTracker - extracted from
// MapClientSession specifically so this invariant is provable without a real TCP session, a
// background reader task, or an uncontrolled real-time hammering window (the previous
// MapClientSessionMonsterMovementTests.VisibleActorTracker_ConcurrentDiscoveryRemovalAndClear_
// ProducesNoCorruptionOrDuplicateDiscovery mixed this concurrency invariant with TCP backpressure/
// load timing and was a known source of CI flakiness - OperationCanceledException from
// WriteBoundedAsync under load, unrelated to the tracker's own correctness).
//
// The core invariant under test: TryMarkVisible(id) returns true for EXACTLY ONE caller per
// "visibility generation" (the window between a MarkNotVisible/Clear and the next successful
// TryMarkVisible for that same id) - never zero, never more than one, regardless of how many
// threads race the same id concurrently.
public sealed class VisibleActorTrackerTests
{
    [Fact]
    public void TryMarkVisible_FirstCallForAnId_ReturnsTrue()
    {
        var tracker = new VisibleActorTracker();

        Assert.True(tracker.TryMarkVisible(1));
        Assert.True(tracker.IsActorVisible(1));
    }

    [Fact]
    public void TryMarkVisible_SecondCallForTheSameId_ReturnsFalse()
    {
        var tracker = new VisibleActorTracker();
        tracker.TryMarkVisible(1);

        Assert.False(tracker.TryMarkVisible(1));
    }

    [Fact]
    public void MarkNotVisible_ThenTryMarkVisible_ReturnsTrueAgain()
    {
        var tracker = new VisibleActorTracker();
        tracker.TryMarkVisible(1);
        tracker.MarkNotVisible(1);

        Assert.False(tracker.IsActorVisible(1));
        Assert.True(tracker.TryMarkVisible(1));
    }

    [Fact]
    public void Clear_RemovesEveryTrackedId()
    {
        var tracker = new VisibleActorTracker();
        tracker.TryMarkVisible(1);
        tracker.TryMarkVisible(2);

        tracker.Clear();

        Assert.False(tracker.IsActorVisible(1));
        Assert.False(tracker.IsActorVisible(2));
        Assert.True(tracker.TryMarkVisible(1));
        Assert.True(tracker.TryMarkVisible(2));
    }

    // The actual concurrency invariant: many threads race TryMarkVisible for the SAME id
    // simultaneously - exactly one must observe true (the pre-fix plain HashSet<uint> could
    // corrupt its internal bucket structure or let two racing callers both observe "newly added"
    // under this exact shape of contention). Runs many trials with a real Barrier to maximize the
    // chance of exercising the race window on every run, deterministically asserting the count
    // rather than depending on any wall-clock timing.
    [Fact]
    public void TryMarkVisible_ManyThreadsRaceTheSameId_ExactlyOneObservesTrue()
    {
        const int trials = 200;
        const int threadCount = 8;

        for (var trial = 0; trial < trials; trial++)
        {
            var tracker = new VisibleActorTracker();
            var trueCount = 0;
            var barrier = new Barrier(threadCount);
            var threads = new Thread[threadCount];

            for (var t = 0; t < threadCount; t++)
            {
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait(); // Maximize simultaneous contention on the same id.
                    if (tracker.TryMarkVisible(42)) Interlocked.Increment(ref trueCount);
                });
                threads[t].Start();
            }

            foreach (var thread in threads) thread.Join();

            Assert.Equal(1, trueCount);
            Assert.True(tracker.IsActorVisible(42));
        }
    }

    // Concurrent TryMarkVisible/MarkNotVisible/IsActorVisible/Clear across MANY distinct ids at
    // once, from many threads, for a bounded number of deterministic operations (not a real-time
    // window) - proves no corruption (an exception from a torn internal HashSet) occurs under
    // sustained mixed concurrent access, matching the shape of MapTcpServer's own multi-mob tick
    // fan-out racing a session's other concurrent call sites (packet handling, warp Clear, etc.).
    [Fact]
    public void ConcurrentMixedOperations_AcrossManyIds_NeverThrowsOrCorrupts()
    {
        const int idCount = 50;
        const int threadCount = 6;
        const int operationsPerThread = 2000;

        var tracker = new VisibleActorTracker();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = new Thread[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            var seed = t;
            threads[t] = new Thread(() =>
            {
                var random = new Random(seed);
                try
                {
                    for (var i = 0; i < operationsPerThread; i++)
                    {
                        var id = (uint)random.Next(idCount);
                        switch (random.Next(4))
                        {
                            case 0: tracker.TryMarkVisible(id); break;
                            case 1: tracker.MarkNotVisible(id); break;
                            case 2: tracker.IsActorVisible(id); break;
                            case 3: if (i % 500 == 0) tracker.Clear(); break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();

        Assert.Empty(exceptions);
    }
}
