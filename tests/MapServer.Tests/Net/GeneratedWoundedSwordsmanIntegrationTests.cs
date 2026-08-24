using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class GeneratedWoundedSwordsmanIntegrationTests
{
    [Fact]
    public async Task VisibleRealNpc_ClicksGeneratedOnClickAndStartsQuest21001()
    {
        const string entityId = "npc:iz_int:wounded swordsman#intro_npc02_iz_int";
        var entity = Assert.Single(GeneratedScriptRegistry.Entities, item => item.Id == entityId);
        Assert.Equal(new WorldActorComponent("Wounded Swordsman#intro_npc02_iz_int", "iz_int", 56, 32, 3, 688, 4), entity.Actor);
        var registry = new WorldMapRegistry([], [entity]);
        var actor = Assert.Single(registry.GetVisibleWarpActors("iz_int", 56, 32));
        Assert.True(registry.TryGetInteraction(actor.ActorId, "iz_int", out var bound, out _));
        Assert.Same(entity, bound);

        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync(); await connect;
        await using var stream = client.GetStream();
        var persistence = new RecordingQuestPersistence();
        using var session = new MapClientSession(1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true, "iz_int", 56, 32, registry,
            questPersistence: persistence, accountId: 7, charId: 9);
        var run = session.RunAsync(CancellationToken.None);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        var spawn = await ReadDynamic(stream);
        Assert.Equal(actor.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(19)));
        Assert.Equal((ushort)688, BinaryPrimitives.ReadUInt16LittleEndian(spawn.AsSpan(23)));
        Assert.Equal((byte)3, (byte)(spawn[65] & 0x0f));

        await stream.WriteAsync(ActorPacket(0x0090, actor.ActorId, 8));
        Assert.Equal("[Wounded]\0", Message(await ReadDynamic(stream)));
        Assert.Equal("Wow! Thanks a lot!\0", Message(await ReadDynamic(stream)));
        Assert.Equal("I don't know how this happened to our ship\0", Message(await ReadDynamic(stream)));
        Assert.Equal("but we should go to see the captain.\0", Message(await ReadDynamic(stream)));
        Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));
        Assert.Null(session.ActiveScriptState);
        Assert.Equal(entityId, session.ActiveGeneratedScriptEntityId);

        await stream.WriteAsync(ActorPacket(0x00b9, actor.ActorId, 7));
        Assert.Equal("[Wounded]\0", Message(await ReadDynamic(stream)));
        Assert.Equal("... ohh, it seems my body is too injured.\0", Message(await ReadDynamic(stream)));
        Assert.Equal("Maybe you can go without me?\0", Message(await ReadDynamic(stream)));
        var quest = await ReadExact(stream, IroQuestPackets.AddQuestLength);
        Assert.Equal((short)0x0b0c, BinaryPrimitives.ReadInt16LittleEndian(quest));
        Assert.Equal(21001u, BinaryPrimitives.ReadUInt32LittleEndian(quest.AsSpan(2)));
        Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));

        await stream.WriteAsync(ActorPacket(0x00b9, actor.ActorId, 7));
        var cutin = await ReadExact(stream, 67);
        Assert.Equal((short)0x01b3, BinaryPrimitives.ReadInt16LittleEndian(cutin));
        Assert.Equal("tutorial02.BMP", System.Text.Encoding.ASCII.GetString(cutin.AsSpan(2, 64)).TrimEnd('\0'));
        Assert.Equal((byte)4, cutin[66]);
        Assert.Equal("^4d4dff!- Information -!^000000\0", Message(await ReadDynamic(stream)));
        Assert.Equal("NPC Quest Received.\0", Message(await ReadDynamic(stream)));
        Assert.Equal("^4d4dffQuestinfo Shortcut is Alt + U^000000\0", Message(await ReadDynamic(stream)));
        Assert.Equal("You can check your quest status there anytime.\0", Message(await ReadDynamic(stream)));
        Assert.Equal((short)0x00b6, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));
        Assert.Equal(entityId, session.ActiveGeneratedScriptEntityId);
        await stream.WriteAsync(ActorPacket(0x0146, actor.ActorId, 7));
        var clear = await ReadExact(stream, 67); Assert.Equal((byte)255, clear[66]);
        Assert.Equal(CharacterQuestStatus.Active, persistence.State);
        Assert.Null(session.ActiveScriptState);

        client.Close(); await run.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
    }

    private static byte[] ActorPacket(short type, uint id, int length) { var packet = new byte[length]; BinaryPrimitives.WriteInt16LittleEndian(packet, type); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), id); packet[^1] = 0xaa; return packet; }
    private static string Message(byte[] packet) => System.Text.Encoding.ASCII.GetString(packet.AsSpan(8));
    private static async Task<byte[]> ReadDynamic(Stream stream) { var header = await ReadExact(stream, 4); var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)); return [.. header, .. await ReadExact(stream, length - 4)]; }
    private static async Task<byte[]> ReadExact(Stream stream, int length) { var data = new byte[length]; await stream.ReadExactlyAsync(data); return data; }

    private sealed class RecordingQuestPersistence : ICharacterQuestPersistence
    {
        public CharacterQuestStatus State { get; private set; } = CharacterQuestStatus.Absent;
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint questId, CancellationToken cancellationToken) => Task.FromResult<CharacterQuestStatus?>(State);
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus state, CancellationToken cancellationToken) { State = state; return Task.FromResult(true); }
    }
}
