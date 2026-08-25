using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Testing;

// Shared helper for deterministically driving MapClientSession's background _movementLoop through
// an entire walk using a ControllableTimeProvider, instead of a real-time sleep. Any test that
// starts a walk via HandleIroMovementAsync (an authenticated session, either through
// CompleteIroAuthenticationAsync or the test-facing iroAuthenticated: true constructor - both now
// start the scheduler via MapClientSession.EnsureRuntimeLoopsStarted) and needs to observe a
// deferred arrival (warp or OnTouch script trigger) should use this instead of hand-rolling clock
// advancement, so every such test shares the exact same synchronization.
internal static class MovementSchedulerTestHelpers
{
    // Deliberately does NOT try to detect "arrival" itself (neither via TcpClient.Available nor via
    // the walk's transient trigger cell): SendSameServerWarpAsync calls TeleportTo(...) - which
    // updates the session's authoritative position to the POST-warp destination - before it awaits
    // WriteAsync(...) for the map-change packet. That leaves a real window where the session's
    // position already reflects the far side of the warp while nothing has been written to the
    // socket yet, so both of those signals are racy as an "are we done" condition. Instead: wait
    // for the scheduler to arm its first fake timer, then advance fake time far enough to cover the
    // whole walk in one deterministic step, and let the caller's own bounded network read (already
    // required for every assertion in these tests) be the actual completion signal for whatever
    // side effect the arrival produces.
    public static async Task AdvanceEntireWalkAsync(ControllableTimeProvider clock, int cellCount)
    {
        // The movement loop's very first CreateTimer call for this walk is itself asynchronous
        // relative to the packet handler that started it (HandleIroMovementAsync releases
        // _movementSignal and returns; RunMovementLoopAsync wakes and reschedules on its own
        // background task) - so the first registration must be awaited before advancing time, or
        // the advance can race ahead of a timer that hasn't been armed yet.
        await clock.WaitForRegistrationAfterAsync(-1).WaitAsync(TimeSpan.FromSeconds(5));

        // One atomic clock step covering the entire known route: CharacterMovementState.AdvanceTo
        // stops exactly at the walk's destination regardless of how far past it `now` is, so there
        // is no risk of overshooting into unrelated future scheduling by advancing generously here.
        var routeDuration = TimeSpan.FromMilliseconds((long)cellCount * MovementSpeedCalculator.DefaultWalkSpeedMs);
        await clock.AdvanceAsync(routeDuration);
    }
}
