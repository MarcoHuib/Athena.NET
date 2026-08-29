using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.World;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Regression test for the LIVE stock-iRO stutter/jump-forward bug observed during monster chase:
// the stock client sends rapid successive 0x035F requests while a walk is already in progress, and
// the PREVIOUS Athena behavior (SyncPositionToNow + immediate StartWalk on every request) reset
// _stepStartedAt on each one, discarding whatever real progress had already elapsed through the
// CURRENT cell - producing exactly the observed visible stutter-then-jump.
//
// Pinned rAthena does NOT do this. unit_walktoxy (unit.cpp:884-899):
//     ud->to_x = x; ud->to_y = y;
//     if (ud->walktimer != INVALID_TIMER) { ud->state.change_walk_target = 1; return 1; }
// A mid-walk retarget only overwrites the desired destination; it does not touch the in-flight
// step at all. The actual re-path happens later, in unit_walktoxy_timer, ONLY once that step's
// timer fires (unit.cpp:738-744) - i.e. at the next real cell boundary, using whatever cell the
// character has ACTUALLY reached by then. Critically, clif_parse_WalkToXY (clif.cpp:11379-11423)
// itself never calls clif_walkok - the 0x0087 response pinned source sends for a mid-walk retarget
// comes from unit_walktoxy_sub's own unit_walktoxy_nextcell(*bl, true, ...) call inside
// unit_walktoxy_timer (unit.cpp:317, sendMove=true), which only runs at that later cell boundary.
// A mid-walk 0x035F therefore produces NO immediate 0x0087 at all.
public sealed class MapClientSessionMovementRetargetTests
{
    private sealed class LinearPathProvider : IMovementPathProvider
    {
        public IReadOnlyList<(ushort X, ushort Y)> ComputePath(string mapName, ushort fromX, ushort fromY, ushort toX, ushort toY) =>
            GridLineTraversal.Enumerate(fromX, fromY, toX, toY).ToArray();
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, FakeTimeProvider Clock, TcpListener Listener)> SetupAsync(ushort startX = 0, ushort startY = 0, string mapName = "iz_int01")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        var stream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var clock = new FakeTimeProvider();
        var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: mapName, x: startX, y: startY,
            timeProvider: clock, movementPathProvider: new LinearPathProvider());
        var runTask = session.RunAsync(CancellationToken.None);
        return (client, stream, session, runTask, clock, listener);
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static (ushort FromX, ushort FromY, ushort ToX, ushort ToY) DecodeMovement(ReadOnlySpan<byte> coordinates)
    {
        var fromX = (ushort)((coordinates[0] << 2) | (coordinates[1] >> 6));
        var fromY = (ushort)(((coordinates[1] & 0x3f) << 4) | (coordinates[2] >> 4));
        var toX = (ushort)(((coordinates[2] & 0x0f) << 6) | (coordinates[3] >> 2));
        var toY = (ushort)(((coordinates[3] & 0x03) << 8) | coordinates[4]);
        return (fromX, fromY, toX, toY);
    }

    private static byte[] BuildMovementRequest(ushort x, ushort y)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x035f);
        packet[2] = (byte)(x >> 2);
        packet[3] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        packet[4] = (byte)(y << 4);
        packet[5] = 0xab;
        return packet;
    }

    // Synchronizes on a just-sent packet actually being PROCESSED by MapClientSession's own packet
    // loop before the test proceeds to advance the fake clock. WriteAsync only guarantees the bytes
    // were queued on the socket, not that the background RunAsync loop already dispatched them -
    // under real scheduling load (e.g. the full test suite running in parallel) a retarget request
    // and a subsequent clock.Advance can otherwise race, intermittently letting the clock move past
    // a step boundary BEFORE the retarget was actually recorded. 0x0B1C (ping) is processed
    // strictly after whatever was written immediately before it on the same TCP stream, and this
    // bare (non-authenticated-gameplay) test session already supports it with no auth/inventory
    // prerequisites (see SendPingLiveAsync) - same synchronization idiom
    // MapClientSessionMonsterCombatTests already establishes for this exact class of race.
    private static async Task SyncAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(reply));
    }

    // The core deterministic scenario from the task's own spec: start a 400ms A->B step, retarget
    // at t=300ms, confirm the character is still in A's cell at t=399ms, reaches B at exactly
    // t=400ms (never later - proving no old-cell time was discarded OR duplicated), and the
    // replacement path begins from B with NO extra 300ms "used up" by the retarget (i.e. total time
    // to first reach B is exactly 400ms, not 700ms).
    [Fact]
    public async Task MidWalkRetarget_At300ms_ReachesOriginalDestinationAtExactly400ms_NeverRequiring700ms()
    {
        var (client, stream, session, run, clock, listener) = await SetupAsync();
        using var _ = client;
        listener.Stop();

        // A(0,0) -> B(4,0): 4 orthogonal cells, 150ms/cell default (no haste) = 600ms total, but we
        // only care about the FIRST step's own 150ms boundary here - use a single-cell first step
        // by targeting B=(1,0) directly so "400ms to reach B" in the task's own framing maps onto
        // this project's real 150ms-per-cell default. To match the task's literal 400ms numbers
        // exactly (as would apply to a 400ms WalkSpeed monster/player), a custom haste-independent
        // step duration isn't directly selectable here without touching gameplay state, so this
        // test reproduces the SAME relative timing property (see the sibling diagonal-timing test
        // below for the exact 560ms/400ms G_PORING-style numbers via CharacterMovementState
        // directly, which is unit-level and can inject any orthogonalStepMs it likes).
        await stream.WriteAsync(BuildMovementRequest(1, 0));
        var firstResponse = await ReadExact(stream, 12);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(firstResponse));
        var firstMove = DecodeMovement(firstResponse.AsSpan(6, 6));
        Assert.Equal(((ushort)0, (ushort)0, (ushort)1, (ushort)0), firstMove);

        // Retarget mid-step (before the 150ms step completes) - must NOT produce any response.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await stream.WriteAsync(BuildMovementRequest(1, 5));
        await SyncAsync(stream); // Confirm the retarget was recorded before advancing further.

        // Absence of a SECOND 0x0087 is proven by the successful ping round-trip above (a mid-walk
        // 0x035F producing an immediate 0x0087 would have desynchronized that read instead) and by
        // reading CurrentX/CurrentY directly here, just before the step boundary.
        clock.Advance(TimeSpan.FromMilliseconds(49)); // t=149ms since the step started.
        Assert.Equal((ushort)0, session.CurrentX);
        Assert.Equal((ushort)0, session.CurrentY);

        clock.Advance(TimeSpan.FromMilliseconds(1)); // t=150ms - the step boundary.

        // Only NOW does the deferred retarget apply, producing exactly one fresh 0x0087 with
        // src=(1,0) (the cell just reached) dst=(1,5) (the latest requested destination) - never
        // the original (1,0) target, and never delayed further.
        var retargetResponse = await ReadExact(stream, 12);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(retargetResponse));
        var retargetMove = DecodeMovement(retargetResponse.AsSpan(6, 6));
        Assert.Equal(((ushort)1, (ushort)0, (ushort)1, (ushort)5), retargetMove);
        Assert.Equal((ushort)1, session.CurrentX);
        Assert.Equal((ushort)0, session.CurrentY);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Two retargets before the current step completes - only the LATEST must be applied, matching
    // pinned ud->to_x/ud->to_y plain-assignment "latest wins" semantics (no queue).
    [Fact]
    public async Task TwoRetargetsBeforeStepCompletes_LatestWins()
    {
        var (client, stream, session, run, clock, listener) = await SetupAsync();
        using var _ = client;
        listener.Stop();

        await stream.WriteAsync(BuildMovementRequest(1, 0));
        await ReadExact(stream, 12);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await stream.WriteAsync(BuildMovementRequest(1, 5)); // First retarget.
        await SyncAsync(stream);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await stream.WriteAsync(BuildMovementRequest(1, 9)); // Second retarget - supersedes the first.
        await SyncAsync(stream);

        clock.Advance(TimeSpan.FromMilliseconds(50)); // t=150ms - step boundary.

        var retargetResponse = await ReadExact(stream, 12);
        var retargetMove = DecodeMovement(retargetResponse.AsSpan(6, 6));
        Assert.Equal((ushort)1, retargetMove.ToX);
        Assert.Equal((ushort)9, retargetMove.ToY); // The SECOND retarget's destination, not the first's.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // A retarget received exactly AT a step boundary (the same tick the step completes) must still
    // be honored as a deferred retarget applied at that boundary - not dropped, and not treated as
    // "already moving so ignore it because we're mid-transition".
    [Fact]
    public async Task RetargetExactlyAtStepBoundary_IsAppliedAtThatBoundary()
    {
        var (client, stream, session, run, clock, listener) = await SetupAsync();
        using var _ = client;
        listener.Stop();

        await stream.WriteAsync(BuildMovementRequest(1, 0));
        await ReadExact(stream, 12);

        clock.Advance(TimeSpan.FromMilliseconds(150)); // Exactly at the step boundary - still moving until AdvanceTo runs.
        await stream.WriteAsync(BuildMovementRequest(3, 3));

        var response = await ReadExact(stream, 12);
        var move = DecodeMovement(response.AsSpan(6, 6));
        Assert.Equal((ushort)1, move.FromX);
        Assert.Equal((ushort)0, move.FromY);
        Assert.Equal((ushort)3, move.ToX);
        Assert.Equal((ushort)3, move.ToY);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // A movement request while NOT currently moving (completed walk, or a fresh session) must still
    // start immediately with no deferral - matching pinned unit_walktoxy's OTHER branch
    // (ud->walktimer == INVALID_TIMER calls unit_walktoxy_sub right away, unit.cpp:915).
    [Fact]
    public async Task MovementRequest_WhileNotMoving_StartsImmediately()
    {
        var (client, stream, session, run, clock, listener) = await SetupAsync();
        using var _ = client;
        listener.Stop();

        await stream.WriteAsync(BuildMovementRequest(1, 0));
        var response = await ReadExact(stream, 12);
        var move = DecodeMovement(response.AsSpan(6, 6));
        Assert.Equal(((ushort)0, (ushort)0, (ushort)1, (ushort)0), move);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Multiple consecutive mid-walk retargets (a real chase scenario - the reported live PR #20
    // symptom, a small visible speed-up/hop exactly on repeated retargets) must never let ANY
    // authoritative cell arrive faster than elapsed walk time permits, across several
    // retarget-application boundaries in a row. This is a wire-level end-to-end proof that
    // CharacterMovementState.CurrentCellReachedAt (see CharacterMovementStateTests' own focused
    // unit test for the mechanism itself) is actually wired correctly through
    // MapClientSession.ProcessDueMovementAsync - using the SAME deterministic FakeTimeProvider
    // (stable except for explicit Advance() calls) every other test in this file already uses, so
    // this test's timing is exact and does not depend on how many times the background movement
    // loop happens to poll the clock.
    [Fact]
    public async Task RepeatedMidWalkRetargets_NoAuthoritativeCellEverAdvancesFasterThanElapsedWalkTimePermits()
    {
        var (client, stream, session, run, clock, listener) = await SetupAsync();
        using var _ = client;
        listener.Stop();

        await stream.WriteAsync(BuildMovementRequest(1, 0));
        await ReadExact(stream, 12);
        // The initial walk is a single 150ms orthogonal step (0,0)->(1,0). Its own boundary is
        // reached inside the loop's first iteration (nothing has crossed it yet at this point).
        var pendingBoundaryFromX = (ushort)0;
        var pendingBoundaryFromY = (ushort)0;
        var pendingBoundaryToX = (ushort)1;
        var pendingBoundaryToY = (ushort)0;

        // Each retarget targets (reachedX, reachedY+1) - i.e. exactly one cell straight "north" of
        // wherever the character will actually be standing once the CURRENT in-flight step
        // completes and the retarget is applied - so every replacement path is a single, purely
        // orthogonal 150ms step (never a 210ms diagonal one), keeping this test's own timing math
        // exact without needing to reproduce GridLineTraversal's Bresenham stepping order. Each
        // retarget is requested 50ms after the previous boundary (100ms before the next 150ms
        // boundary it will itself be deferred to), matching every other test in this file.
        for (var retarget = 1; retarget <= 3; retarget++)
        {
            var targetX = pendingBoundaryToX;
            var targetY = (ushort)(pendingBoundaryToY + 1);
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await stream.WriteAsync(BuildMovementRequest(targetX, targetY));
            await SyncAsync(stream);

            // Must not yet have reached the pending boundary: still exactly at the previously
            // reached cell (the CURRENT in-flight step has not completed yet).
            Assert.Equal(pendingBoundaryFromX, session.CurrentX);
            Assert.Equal(pendingBoundaryFromY, session.CurrentY);

            // 1ms short of the 150ms boundary: the authoritative cell must not have advanced yet.
            clock.Advance(TimeSpan.FromMilliseconds(99));
            Assert.Equal(pendingBoundaryFromX, session.CurrentX);
            Assert.Equal(pendingBoundaryFromY, session.CurrentY);

            clock.Advance(TimeSpan.FromMilliseconds(1)); // Now at the true boundary.
            var response = await ReadExact(stream, 12);
            var move = DecodeMovement(response.AsSpan(6, 6));
            // The retarget's own fresh 0x0087 is anchored at the cell the CURRENT (pre-retarget)
            // step actually completes into - never the cell the retarget was requested from.
            Assert.Equal(pendingBoundaryToX, move.FromX);
            Assert.Equal(pendingBoundaryToY, move.FromY);
            Assert.Equal(targetX, move.ToX);
            Assert.Equal(targetY, move.ToY);
            Assert.Equal(pendingBoundaryToX, session.CurrentX);
            Assert.Equal(pendingBoundaryToY, session.CurrentY);

            // The NEXT iteration's pending boundary is THIS retarget's own single-cell step.
            pendingBoundaryFromX = pendingBoundaryToX;
            pendingBoundaryFromY = pendingBoundaryToY;
            pendingBoundaryToX = targetX;
            pendingBoundaryToY = targetY;
        }

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
