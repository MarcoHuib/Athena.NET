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
        using var session = new MapClientSession(
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
        Assert.Contains(persistence.Saves, save => save.MapName == "iz_int03" && save.X == 51 && save.Y == 30);
        Assert.False(runTask.IsCompleted);

        await clientStream.WriteAsync(new byte[] { 0x7d, 0x00, 0xba });
        var actorHeader = new byte[4];
        await clientStream.ReadExactlyAsync(actorHeader);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(actorHeader));
        var actorLength = BinaryPrimitives.ReadUInt16LittleEndian(actorHeader.AsSpan(2));
        var actorRemainder = new byte[actorLength - actorHeader.Length];
        await clientStream.ReadExactlyAsync(actorRemainder);
        await clientStream.WriteAsync(BuildMovementRequest(58, 28));

        var movementAfterWarp = new byte[12];
        await clientStream.ReadExactlyAsync(movementAfterWarp);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movementAfterWarp));
        Assert.Equal(
            ((ushort)51, (ushort)30, (ushort)58, (ushort)28),
            DecodeMovement(movementAfterWarp.AsSpan(6, 6)));
        Assert.Equal((ushort)58, session.CurrentX);
        Assert.Equal((ushort)28, session.CurrentY);
        Assert.False(runTask.IsCompleted);

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(persistence.Saves, save => save.MapName == "iz_int03" && save.X == 58 && save.Y == 28);
        listener.Stop();
    }

    private sealed class RecordingPositionPersistence : ICharacterPositionPersistence
    {
        public List<(uint AccountId, uint CharId, string MapName, ushort X, ushort Y)> Saves { get; } = new();

        public Task<bool> SavePositionAsync(
            uint accountId,
            uint charId,
            string mapName,
            ushort x,
            ushort y,
            CancellationToken cancellationToken)
        {
            Saves.Add((accountId, charId, mapName, x, y));
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
