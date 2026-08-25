using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.World;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Regression test for the diagnosed movement-stutter bug: HandleIroMovementAsync previously jumped
// the server-authoritative position straight to a movement request's destination, so a second
// request arriving before the stock client had visually finished the first walk would retarget from
// a cell the client was never actually shown reaching. Pinned rAthena instead advances the unit's
// position one cell at a time (unit_walktoxy_timer, unit.cpp:542) and re-paths a mid-walk retarget
// from whatever cell has ACTUALLY been reached by then (unit_walktoxy, unit.cpp:894-899) - proven
// independently by ai/map-server.md's own already-documented capture trace (frames 425/435/448:
// second click's own reported source is an intermediate cell on the first route, never the
// first click's destination).
public sealed class MapClientSessionMovementRetargetTests
{
    private sealed class LinearPathProvider : IMovementPathProvider
    {
        public IReadOnlyList<(ushort X, ushort Y)> ComputePath(string mapName, ushort fromX, ushort fromY, ushort toX, ushort toY) =>
            GridLineTraversal.Enumerate(fromX, fromY, toX, toY).ToArray();
    }

    [Fact]
    public async Task SecondMovementRequestMidWalk_RetargetsFromActualCurrentCell_NotPreviousDestination()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var clock = new FakeTimeProvider();
        using var session = new MapClientSession(
            1,
            serverClient,
            connector,
            iroAuthenticated: true,
            mapName: "iz_int01",
            x: 0,
            y: 0,
            timeProvider: clock,
            movementPathProvider: new LinearPathProvider());
        var runTask = session.RunAsync(CancellationToken.None);

        // First click: A(0,0) -> B(8,0). Default 150ms/cell, no haste active.
        await clientStream.WriteAsync(BuildMovementRequest(8, 0));
        var firstResponse = new byte[12];
        await clientStream.ReadExactlyAsync(firstResponse);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(firstResponse));
        var firstMove = DecodeMovement(firstResponse.AsSpan(6, 6));
        Assert.Equal(((ushort)0, (ushort)0, (ushort)8, (ushort)0), firstMove);

        // Advance real time by 2 cells' worth (300ms) - the character has now actually reached
        // (2,0), not (8,0) and not (0,0).
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal((ushort)2, session.CurrentX);
        Assert.Equal((ushort)0, session.CurrentY);

        // Second click before the first walk completes: target D(2,5).
        await clientStream.WriteAsync(BuildMovementRequest(2, 5));
        var secondResponse = new byte[12];
        await clientStream.ReadExactlyAsync(secondResponse);
        var secondMove = DecodeMovement(secondResponse.AsSpan(6, 6));

        // The server's own reported "from" must be the ACTUAL current cell (2,0) - not (8,0) (the
        // previous destination Athena used to pretend it had already reached) and not (0,0) (the
        // original start).
        Assert.Equal(((ushort)2, (ushort)0, (ushort)2, (ushort)5), secondMove);

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
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
}
