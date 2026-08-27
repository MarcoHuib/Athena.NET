using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionWarpTests
{
    [Fact]
    public async Task MovementIntoTutorialDoor_SendsMoveThenMapChangeAndContinuesOnDestination()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var persistence = new RecordingPositionPersistence();
        await using var session = new MapClientSession(
            1,
            serverClient,
            connector,
            iroAuthenticated: true,
            mapName: "iz_int03",
            x: 22,
            y: 31,
            positionPersistence: persistence);
        var runTask = session.RunAsync(CancellationToken.None);

        // The requested target lies beyond the real door area. The direct grid route
        // first enters it at (26,30), so the client need not click the portal tile.
        await clientStream.WriteAsync(BuildMovementRequest(29, 29));

        var movement = new byte[12];
        await clientStream.ReadExactlyAsync(movement);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movement));
        var movementCoordinates = DecodeMovement(movement.AsSpan(6, 6));
        Assert.Equal(((ushort)22, (ushort)31, (ushort)26, (ushort)30), movementCoordinates);

        var mapChange = new byte[22];
        await clientStream.ReadExactlyAsync(mapChange);
        Assert.Equal((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(mapChange));
        Assert.Equal((ushort)51, BinaryPrimitives.ReadUInt16LittleEndian(mapChange.AsSpan(18)));
        Assert.Equal((ushort)30, BinaryPrimitives.ReadUInt16LittleEndian(mapChange.AsSpan(20)));
        Assert.Equal("iz_int03", session.CurrentMapName);
        Assert.Equal((ushort)51, session.CurrentX);
        Assert.Equal((ushort)30, session.CurrentY);

        // SendSameServerWarpAsync writes the 0x0091 map-change packet BEFORE awaiting
        // PersistPositionIfDirtyAsync, so the client can legitimately observe the packet above
        // before the save has run - await the explicit completion signal (not the unsynchronized
        // Saves list, which SavePositionAsync may still be concurrently appending to) rather than
        // asserting on it immediately.
        var persisted = await persistence.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("iz_int03", persisted.MapName);
        Assert.Equal((ushort)51, persisted.X);
        Assert.Equal((ushort)30, persisted.Y);
        Assert.False(runTask.IsCompleted);

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Regression for requirement 7 of the mid-walk-retarget fix: a retarget that steers AWAY from
    // a warp cell the ORIGINAL route would have crossed must fully replace the pending arrival - no
    // stale warp may fire just because the OLD path (computed at click time) once intersected one.
    // Pinned unit_walktoxy_timer re-evaluates npc_touch_area_allnpc/warp checks fresh at every cell
    // it actually reaches (unit.cpp:684-699), never against a route that was abandoned mid-walk.
    [Fact]
    public async Task RetargetAwayFromADoor_MidWalk_NeverWarps_ReplacesThePendingArrival()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var persistence = new RecordingPositionPersistence();
        // ControllableTimeProvider (not World.FakeTimeProvider, which only overrides GetUtcNow) is
        // required here: RunMovementLoopAsync schedules its next per-cell wake via
        // Task.Delay(delay, TimeProvider, ...), which calls TimeProvider.CreateTimer - a provider
        // that doesn't override CreateTimer falls back to real wall-clock timers regardless of what
        // GetUtcNow() reports, so a plain FakeTimeProvider.Advance would leave the walk stuck after
        // only whatever cell(s) real background scheduling happened to race through.
        var clock = new Athena.Net.MapServer.Tests.Testing.ControllableTimeProvider();
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "iz_int03", x: 22, y: 31,
            positionPersistence: persistence, timeProvider: clock);
        var runTask = session.RunAsync(CancellationToken.None);

        // Same route toward the door as the sibling test above - click (29,29), whose direct grid
        // route crosses the door at (26,30) after 4 cells (default 150ms/cell = 600ms total to
        // reach the door cell).
        await clientStream.WriteAsync(BuildMovementRequest(29, 29));
        var movement = new byte[12];
        await clientStream.ReadExactlyAsync(movement);
        Assert.Equal(((ushort)22, (ushort)31, (ushort)26, (ushort)30), DecodeMovement(movement.AsSpan(6, 6)));

        // Retarget mid-walk, before the door cell is reached, toward a destination that never
        // crosses it - almost straight down from the second path cell, away from the door entirely.
        // Drive the clock to that FIRST cell boundary deterministically: capture the registration
        // generation before advancing so we can prove the loop has already rearmed its next timer
        // (i.e. the previous cell's callback, and everything synchronous inside it - including
        // ConsumePendingRetarget - has fully run) before trusting anything that happened as a
        // result. AdvanceAsync itself only guarantees the due callback was invoked, NOT that the
        // async continuations chained after that invocation (the rest of ProcessDueMovementAsync,
        // and the loop's own re-registration of the following step's timer) have completed yet.
        var generationBeforeFirstBoundary = clock.RegistrationGeneration;
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150)); // Exactly one cell - still far from the door.
        await clock.WaitForRegistrationAfterAsync(generationBeforeFirstBoundary).WaitAsync(TimeSpan.FromSeconds(5));

        // Now mid-walk (one cell in), record the retarget.
        await clientStream.WriteAsync(BuildMovementRequest(22, 40));

        // Synchronize on the retarget having actually been recorded on CharacterMovementState (see
        // MapClientSessionMovementRetargetTests.SyncAsync's own doc comment for why a bare
        // WriteAsync alone does not guarantee the packet was processed yet) before driving the clock
        // toward the NEXT boundary, where the retarget is expected to take effect.
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var retargetRecordedPing = new byte[2];
        await clientStream.ReadExactlyAsync(retargetRecordedPing);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(retargetRecordedPing));

        // Drive the clock to the SECOND cell boundary - the first one reached AFTER the retarget was
        // recorded, and per this fix's own contract (CharacterMovementState.AdvanceTo's early-stop
        // and ConsumePendingRetarget), exactly where the retarget must be applied: neither before
        // (the in-flight step's remaining time must be honored) nor deferred past it (no silently
        // continuing along the stale original path for extra cells). The step in flight when the
        // retarget was recorded is the ORIGINAL route's (23,31)->(24,30) - a DIAGONAL step
        // (150ms*14/10=210ms, per the "Movement retarget deferred" diagnostic's own
        // currentStepDueAt=360ms above: 150ms already elapsed + this 210ms step), not another plain
        // 150ms orthogonal step - advancing only 150ms here would fall short of that deadline.
        var generationBeforeRetargetBoundary = clock.RegistrationGeneration;
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(210));
        await clock.WaitForRegistrationAfterAsync(generationBeforeRetargetBoundary).WaitAsync(TimeSpan.FromSeconds(5));

        // The retarget must have applied exactly here: a fresh 0x0087 for the replacement path
        // appears now, sourced from the cell just reached - drain forward from the stream (bounded
        // by a ping round-trip, so this loop cannot hang if the response never arrives) until we
        // find it. Visibility-refresh packets for newly-visible NPC/warp actors near the replacement
        // route may legitimately interleave, but a 0x0091 map-change must never appear - that would
        // mean the original door's stale pending arrival survived the retarget.
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var sawRetargetResponse = false;
        while (true)
        {
            var header = new byte[2];
            await clientStream.ReadExactlyAsync(header);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
            if (opcode == 0x0b1d) break; // The ping reply itself - stop draining.
            Assert.NotEqual(0x0091, opcode); // No stale map-change may ever appear.
            if (opcode == 0x0087)
            {
                sawRetargetResponse = true;
                await clientStream.ReadExactlyAsync(new byte[10]); // Rest of the fixed 12-byte packet.
            }
            else
            {
                // Every other packet type this path can emit (NPC/warp/monster actor entries) is
                // variable-length with its own length prefix as the next 2 bytes.
                var lengthBytes = new byte[2];
                await clientStream.ReadExactlyAsync(lengthBytes);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
                await clientStream.ReadExactlyAsync(new byte[length - 4]);
            }
        }
        Assert.True(sawRetargetResponse);

        // Drive the clock the rest of the way to the replacement path's own destination, one
        // registration-synchronized boundary at a time, so every intermediate cell (each of which
        // re-evaluates warp/OnTouch fresh, per requirement 7) is proven reached deterministically
        // rather than assumed via one large blind jump or a live (and racy - CurrentX/Y call
        // SyncPositionToNow, which mutates state outside _movementGate) poll of session state.
        //
        // The retarget is applied at (24,30) - the cell the ORIGINAL route's diagonal step actually
        // landed on - by recomputing GridLineTraversal from there to (22,40):
        // [(24,30),(24,31),(24,32),(23,33),(23,34),(23,35),(23,36),(23,37),(22,38),(22,39),(22,40)],
        // verified by hand via GridLineTraversal.Enumerate's own Bresenham stepping for this exact
        // (dx=-2,dy=10) pair: steps 1-2 orthogonal(150ms), step 3 diagonal(210ms), steps 4-7
        // orthogonal(150ms), step 8 diagonal(210ms), steps 9-10 orthogonal(150ms). None of these 10
        // steps were consumed by the boundary AdvanceAsync above (that call only reached the
        // boundary AT (24,30) itself, where StartWalk installs this replacement path fresh) - all 10
        // remain to drive here.
        // The LAST step is driven separately below: once it completes, IsMoving becomes false and
        // NextStepDueAt goes back to null, so RunMovementLoopAsync falls back to
        // _movementSignal.WaitAsync instead of another Task.Delay/CreateTimer - no further
        // registration bump ever comes for this walk, so waiting on one after that final advance
        // would hang forever.
        int[] intermediateStepMs = [150, 150, 210, 150, 150, 150, 150, 210, 150];
        foreach (var stepMs in intermediateStepMs)
        {
            var before = clock.RegistrationGeneration;
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(stepMs));
            await clock.WaitForRegistrationAfterAsync(before).WaitAsync(TimeSpan.FromSeconds(5));
        }

        // The replacement route has no warp/script arrival of its own (ResolveMovementTarget found
        // none along it), so reaching its final cell sends nothing further on the wire - per
        // ProcessDueMovementAsync above, both appliedRetarget and arrival are null for every one of
        // these ordinary intermediate/final crossings. Confirm that directly: a ping sent now must
        // get an immediate reply with no map-change (or anything else) ahead of it. The ping
        // round-trip itself is the synchronization here (bounded, unlike a registration wait that
        // would never resolve after the walk's last step).
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150)); // Final step: reaches (22,40).
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var finalPing = new byte[2];
        await clientStream.ReadExactlyAsync(finalPing);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(finalPing));

        Assert.Equal("iz_int03", session.CurrentMapName); // Never warped.
        Assert.Equal((ushort)22, session.CurrentX);
        Assert.Equal((ushort)40, session.CurrentY); // Reached the REPLACEMENT destination.

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    private sealed class RecordingPositionPersistence : ICharacterPositionPersistence
    {
        public TaskCompletionSource<(uint AccountId, uint CharId, string MapName, ushort X, ushort Y)> Saved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> SavePositionAsync(
            uint accountId,
            uint charId,
            string mapName,
            ushort x,
            ushort y,
            CancellationToken cancellationToken)
        {
            Saved.TrySetResult((accountId, charId, mapName, x, y));
            return Task.FromResult(true);
        }
    }

    private static (ushort FromX, ushort FromY, ushort ToX, ushort ToY) DecodeMovement(
        ReadOnlySpan<byte> coordinates)
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
}
