namespace Athena.Net.MapServer.Tests.Testing;

// Deterministic controllable TimeProvider: GetUtcNow() only advances on explicit AdvanceAsync()
// calls, and AdvanceAsync() also fires any Task.Delay(..., this, ...) timers whose due time has
// been crossed - this is what lets MapClientSession's status/movement schedulers (both built on
// Task.Delay(delay, TimeProvider, cancellationToken), which internally calls CreateTimer) be
// driven deterministically in tests without real waits or a new package dependency.
//
// Synchronization with the scheduler under test is explicit and observable, not real-time polling:
// RegistrationGeneration/WaitForRegistrationAfterAsync let a test await "the scheduler has (re)
// registered its next deadline" as a fact, not a guess bounded by a fixed sleep. There is no
// silent-timeout-success path here - a caller that wants a failure guard wraps the returned Task in
// its own bounded .WaitAsync(...); that timeout is a test-failure guard only, never a synchronization
// mechanism this class relies on internally.
internal sealed class ControllableTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly List<ScheduledCallback> _callbacks = [];
    // Bumped on every CreateTimer registration (Schedule) with a live due time, and on every
    // Cancel. A monotonically increasing counter, not the callback list's Count: Count alone
    // cannot distinguish "a new timer registered" from "a timer that was already there is still
    // there" when registrations and cancellations interleave, which is exactly the ambiguity that
    // made the old _callbacks.Count > 0 poll unreliable.
    private long _registrationGeneration;
    private readonly List<(long Generation, TaskCompletionSource Signal)> _generationWaiters = [];

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    // A snapshot a test can capture BEFORE triggering a scheduler change (e.g. StartStatusAsync),
    // then pass to WaitForRegistrationAfterAsync to deterministically await that specific change's
    // resulting CreateTimer call, however many registrations happen to occur in between.
    public long RegistrationGeneration { get { lock (_gate) return _registrationGeneration; } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ScheduledTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    // Fires every due callback synchronously as of `_now + delta`, in one atomic clock step - no
    // real-time settling afterward. Callers that need to observe the resulting write(s) on the wire
    // await the network read itself (already bounded in every caller), which is the actual
    // completion signal for "the scheduler's reaction was fully processed and flushed."
    public Task AdvanceAsync(TimeSpan delta)
    {
        List<ScheduledCallback> due;
        lock (_gate)
        {
            _now += delta;
            due = [.. _callbacks.Where(c => c.DueAt <= _now)];
            _callbacks.RemoveAll(c => c.DueAt <= _now);
        }
        foreach (var callback in due) callback.Invoke();
        return Task.CompletedTask;
    }

    // Completes the instant a timer registration happens at a generation strictly greater than
    // `afterGeneration` - not a poll, not a fixed real-time wait. Typical use:
    //   var generation = clock.RegistrationGeneration;
    //   await context.StartStatusAsync(...);              // triggers the scheduler's reschedule
    //   await clock.WaitForRegistrationAfterAsync(generation).WaitAsync(TimeSpan.FromSeconds(5));
    //   await clock.AdvanceAsync(...);
    // The .WaitAsync(...) timeout above is the caller's own failure guard, never something this
    // method relies on to make progress.
    public Task WaitForRegistrationAfterAsync(long afterGeneration)
    {
        lock (_gate)
        {
            if (_registrationGeneration > afterGeneration)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _generationWaiters.Add((afterGeneration, signal));
            return signal.Task;
        }
    }

    private void Schedule(ScheduledCallback callback)
    {
        lock (_gate)
        {
            if (callback.DueAt <= _now) { callback.Invoke(); return; }
            _callbacks.Add(callback);
            _registrationGeneration++;
            NotifyRegistrationWaiters();
        }
    }

    private void Cancel(ScheduledCallback callback)
    {
        lock (_gate)
        {
            _callbacks.Remove(callback);
        }
    }

    // Must be called while holding _gate.
    private void NotifyRegistrationWaiters()
    {
        if (_generationWaiters.Count == 0) return;
        var ready = _generationWaiters.Where(w => _registrationGeneration > w.Generation).ToList();
        foreach (var waiter in ready)
        {
            _generationWaiters.Remove(waiter);
            waiter.Signal.TrySetResult();
        }
    }

    private sealed record ScheduledCallback(DateTimeOffset DueAt, TimerCallback Callback, object? State)
    {
        public void Invoke() => Callback(State);
    }

    private sealed class ScheduledTimer(ControllableTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private ScheduledCallback? _current;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_current is not null) owner.Cancel(_current);
            if (dueTime == Timeout.InfiniteTimeSpan) { _current = null; return true; }
            _current = new ScheduledCallback(owner.GetUtcNow() + dueTime, callback, state);
            owner.Schedule(_current);
            return true;
        }

        public void Dispose() { if (_current is not null) owner.Cancel(_current); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
