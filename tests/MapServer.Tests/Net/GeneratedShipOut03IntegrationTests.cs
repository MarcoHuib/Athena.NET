using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class GeneratedShipOut03IntegrationTests
{
    [Fact]
    public async Task RealShipExit_IsVisibleAndExecutesGeneratedSavePointThenWarp()
    {
        var entity = WorldMapRegistry.Tutorial.EntitiesById["warp:iz_int03:ship_out03"];
        var actor = Assert.Single(WorldMapRegistry.Tutorial.GetVisibleWarpActors("iz_int03", 56, 15), item => item.Name == "#ship_out03");
        Assert.Equal(new WorldActorComponent("#ship_out03", "iz_int03", 56, 15, 0, 45), entity.Actor);

        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync(); await connect;
        await using var stream = client.GetStream();
        var persistence = new RecordingPositionPersistence();
        using var session = new MapClientSession(1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true, "iz_int03", 51, 30,
            WorldMapRegistry.Tutorial, positionPersistence: persistence, accountId: 7, charId: 9);
        var run = session.RunAsync(CancellationToken.None);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadDynamic(stream); // #room_in03
        await ReadDynamic(stream); // lying Wounded Swordsman
        await ReadDynamic(stream); // initially cloaked sitting Wounded Swordsman
        Assert.Equal((short)0x08e2, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 27))); // generated guidance toward int_land
        await stream.WriteAsync(MovementPacket(56, 15));
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 12)));
        var shipSpawn = await ReadDynamic(stream);
        Assert.Equal(actor.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(shipSpawn.AsSpan(5)));
        var mapChange = await ReadExact(stream, 22);
        Assert.Equal((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(mapChange));
        Assert.Equal("int_land03.gat", System.Text.Encoding.ASCII.GetString(mapChange.AsSpan(2, 16)).TrimEnd('\0'));
        Assert.Equal(("int_land03", (ushort)77, (ushort)101), persistence.SavePoint);
        Assert.Equal("int_land03", session.CurrentMapName);
        Assert.Equal((ushort)85, session.CurrentX);
        Assert.Equal((ushort)107, session.CurrentY);
        Assert.Null(session.ActiveScriptState);

        client.Close(); await run.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
    }

    private static byte[] MovementPacket(ushort x, ushort y) { var packet = new byte[6]; BinaryPrimitives.WriteInt16LittleEndian(packet, 0x035f); packet[2]=(byte)(x>>2); packet[3]=(byte)((x<<6)|((y>>4)&0x3f)); packet[4]=(byte)(y<<4); packet[5]=0xaa; return packet; }
    private static async Task<byte[]> ReadDynamic(Stream stream) { var header=await ReadExact(stream,4); var length=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)); return [..header,..await ReadExact(stream,length-4)]; }
    private static async Task<byte[]> ReadExact(Stream stream,int length) { var data=new byte[length]; await stream.ReadExactlyAsync(data); return data; }

    private sealed class RecordingPositionPersistence : ICharacterPositionPersistence
    {
        public (string Map, ushort X, ushort Y) SavePoint { get; private set; }
        public Task<bool> SavePositionAsync(uint accountId,uint charId,string map,ushort x,ushort y,CancellationToken cancellationToken)=>Task.FromResult(true);
        public Task<bool> SavePointAsync(uint accountId,uint charId,string map,ushort x,ushort y,CancellationToken cancellationToken) { SavePoint=(map,x,y); return Task.FromResult(true); }
    }
}
