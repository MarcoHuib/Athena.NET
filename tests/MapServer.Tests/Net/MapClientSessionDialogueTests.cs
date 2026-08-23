using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionDialogueTests
{
    [Fact]
    public async Task VisibleInteractableActor_StartsOneSession_SuspendsResumesAndCloses()
    {
        var entity = new WorldEntityDefinition(1, "npc:test:greeter", "Npc", new("Greeter", "test", 10, 10, 0, 45), [],
            [new("OnClick", "test", 10, 10, 0, 0, true, true, ["Message", "Next", "Select", "SetQuest", "Close"], "test menu", [new MessageInstruction("Hello"), new NextInstruction(), new MessageInstruction("Choose"), new SelectInstruction([new("A", [new SetQuestInstruction(21001), new MessageInstruction("Branch A"), new CloseInstruction()]), new("B", [new MessageInstruction("Branch B"), new CloseInstruction()])])])],
            new("test", "test", "fixture", 1));
        var registry = new WorldMapRegistry([new("#door", "test", 11, 10, 0, 0, "test", 12, 10, true, "fixture", 1)], [entity]);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync(); await connect;
        await using var stream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var quests = new RecordingQuestPersistence();
        using var session = new MapClientSession(1, serverClient, connector, true, "test", 10, 10, registry, questPersistence: quests, accountId: 7, charId: 9);
        var run = session.RunAsync(CancellationToken.None);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        var nonInteractableActorId = await ReadActorId(stream);
        var actorId = await ReadActorId(stream);

        await stream.WriteAsync(BuildClientActorPacket(0x0090, uint.MaxValue, 8));
        await stream.WriteAsync(BuildClientActorPacket(0x0090, nonInteractableActorId, 8));
        await Task.Delay(25);
        Assert.Null(session.ActiveScriptState);

        byte[] clickSequence = [0x61, 0x03, 0x01, 0x00, 0x01, 0xaf, .. BuildClientActorPacket(0x0090, actorId, 8)];
        await stream.WriteAsync(clickSequence);
        var firstMessage = await ReadDynamic(stream);
        Assert.Equal("Hello\0", System.Text.Encoding.ASCII.GetString(firstMessage.AsSpan(8)));
        Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));
        Assert.Equal(ScriptExecutionState.WaitingForNext, session.ActiveScriptState);

        await stream.WriteAsync(BuildClientActorPacket(0x00b9, actorId, 7));
        var secondMessage = await ReadDynamic(stream);
        Assert.Equal("Choose\0", System.Text.Encoding.ASCII.GetString(secondMessage.AsSpan(8)));
        var menu = await ReadDynamic(stream);
        Assert.Equal((short)0x00b7, BinaryPrimitives.ReadInt16LittleEndian(menu));
        Assert.Equal("A:B:\0", System.Text.Encoding.ASCII.GetString(menu.AsSpan(8)));
        Assert.Equal(ScriptExecutionState.WaitingForSelection, session.ActiveScriptState);

        await stream.WriteAsync(BuildSelectionPacket(actorId, 1));
        var questAdd = await ReadExact(stream, IroQuestPackets.AddQuestLength);
        Assert.Equal((short)0x0b0c, BinaryPrimitives.ReadInt16LittleEndian(questAdd));
        var branchMessage = await ReadDynamic(stream);
        Assert.Equal("Branch A\0", System.Text.Encoding.ASCII.GetString(branchMessage.AsSpan(8)));
        Assert.Equal((short)0x00b6, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));
        Assert.Null(session.ActiveScriptState);
        Assert.Equal(CharacterQuestStatus.Active, await quests.GetQuestStateAsync(7, 9, 21001, CancellationToken.None));

        await stream.WriteAsync(BuildSelectionPacket(actorId, 1));
        await Task.Delay(50);
        Assert.False(run.IsCompleted);
        client.Close(); await run.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
    }

    [Fact]
    public void UnknownOrNonInteractableActor_HasNoBinding()
    {
        var registry = new WorldMapRegistry([new("#warp", "test", 1, 1, 1, 1, "test", 2, 2, true, "fixture", 1)]);
        Assert.False(registry.TryGetInteraction(999, "test", out _, out _));
        var actor = Assert.Single(registry.GetVisibleWarpActors("test", 1, 1));
        Assert.False(registry.TryGetInteraction(actor.ActorId, "test", out _, out _));
    }

    private static byte[] BuildClientActorPacket(short type, uint actorId, int length)
    {
        var packet = new byte[length]; BinaryPrimitives.WriteInt16LittleEndian(packet, type); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId); packet[^1] = 0xaa; return packet;
    }
    private static byte[] BuildSelectionPacket(uint actorId, byte wireIndex)
    {
        var packet = BuildClientActorPacket(0x00b8, actorId, 8); packet[6] = wireIndex; return packet;
    }
    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4); var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)); var rest = await ReadExact(stream, length - 4); return [.. header, .. rest];
    }
    private static async Task<uint> ReadActorId(Stream stream)
    {
        var header = await ReadExact(stream, 9);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5));
        var actorLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        await ReadExact(stream, actorLength - 9);
        return actorId;
    }
    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length]; await stream.ReadExactlyAsync(buffer); return buffer;
    }

    private sealed class RecordingQuestPersistence : ICharacterQuestPersistence
    {
        private readonly Dictionary<(uint CharId, uint QuestId), CharacterQuestStatus> _states = new();
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint questId, CancellationToken cancellationToken)
            => Task.FromResult<CharacterQuestStatus?>(_states.GetValueOrDefault((charId, questId), CharacterQuestStatus.Absent));
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus state, CancellationToken cancellationToken)
        { _states[(charId, questId)] = state; return Task.FromResult(true); }
    }
}
