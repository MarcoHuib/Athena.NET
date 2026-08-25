using System.Net.Sockets;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Testing;

// Shared helper for deterministically driving MapClientSession's background _movementLoop to a
// target cell using a ControllableTimeProvider, instead of a real-time sleep. Any test that starts
// a walk via HandleIroMovementAsync (an authenticated session, either through
// CompleteIroAuthenticationAsync or the test-facing iroAuthenticated: true constructor - both now
// start the scheduler via MapClientSession.EnsureRuntimeLoopsStarted) and needs to observe a
// deferred arrival (warp or OnTouch script trigger) should use this instead of hand-rolling clock
// advancement, so every such test shares the exact same synchronization.
internal static class MovementSchedulerTestHelpers
{
    // `arrivalClient` is the test's own TcpClient for the session's socket: its Available byte
    // count is the same "network output is the completion signal" contract used everywhere else in
    // these tests (see ControllableTimeProvider's own doc comment) - once the movement loop's
    // arrival branch (SendVisibleWarpActorsAsync / SendSameServerWarpAsync) has actually written a
    // response, there is something to read, which is a strictly stronger and more direct signal
    // than the walk's target cell (a warp's OnTouch/warp arrival action - e.g. TeleportTo - can
    // move the character to an entirely different map before this loop's next position check,
    // making a fixed target coordinate an unreliable stop condition on its own).
    public static async Task AdvanceUntilArrivedAsync(MapClientSession session, ControllableTimeProvider clock, TcpClient arrivalClient, ushort targetX, ushort targetY)
    {
        const int cellDurationMs = MovementSpeedCalculator.DefaultWalkSpeedMs;
        const int maxSteps = 64;

        // The movement loop's very first CreateTimer call for this walk is itself asynchronous
        // relative to the packet handler that started it (HandleIroMovementAsync releases
        // _movementSignal and returns; RunMovementLoopAsync wakes and reschedules on its own
        // background task) - so the very first registration must be awaited before the step loop
        // below assumes one has already happened.
        await clock.WaitForRegistrationAfterAsync(-1).WaitAsync(TimeSpan.FromSeconds(5));

        for (var step = 0; step < maxSteps; step++)
        {
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(cellDurationMs));
            if (arrivalClient.Client.Available > 0 || (session.CurrentX == targetX && session.CurrentY == targetY))
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Movement scheduler did not reach ({targetX},{targetY}) within {maxSteps} cell steps; character is at ({session.CurrentX},{session.CurrentY}).");
    }
}
