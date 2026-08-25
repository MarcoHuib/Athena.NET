using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class GeneratedCaptainCaroccIntegrationTests
{
    private const string EntityId = "npc:int_land03:captain carocc#intro_npc03_03";

    [Fact]
    public async Task VisibleRealNpc_ClicksGeneratedOnClickCompletesQuest21001HealsAndAppliesStatuses()
    {
        var entity = Assert.Single(GeneratedScriptRegistry.Entities, item => item.Id == EntityId);
        Assert.Equal(new WorldActorComponent("Captain Carocc#intro_npc03_03", "int_land03", 78, 103, 5, 873, 0), entity.Actor);
        var registry = new WorldMapRegistry([], [entity]);
        var actor = Assert.Single(registry.GetVisibleWarpActors("int_land03", 78, 103));
        Assert.True(registry.TryGetInteraction(actor.ActorId, "int_land03", out var bound, out _));
        Assert.Same(entity, bound);

        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync(); await connect;
        await using var stream = client.GetStream();
        var questPersistence = new RecordingQuestPersistence(21001, CharacterQuestStatus.Active); // quest 21001 already active, matching the capture's own quest state; 21008 defaults to Absent (case 0).
        var gameplayPersistence = new RecordingGameplayStatePersistence(new(9, 0, 0, 1, 1, 0, 0, 20, 5, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true, "int_land03", 78, 103, registry,
            questPersistence: questPersistence, gameplayStatePersistence: gameplayPersistence, accountId: 7, charId: 9);
        var run = session.RunAsync(CancellationToken.None);
        // Unlike the gameplay-state-free Wounded Swordsman fixture, Captain's script needs
        // CharacterGameplayState loaded (heal/getexp), which only CompleteIroAuthenticationAsync does.
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "int_land03", 78, 103, 0, 0, 0));
        var bootstrap = new byte[29]; await stream.ReadExactlyAsync(bootstrap);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        var spawn = await ReadDynamic(stream);
        Assert.Equal(actor.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5)));
        Assert.Equal((ushort)873, BinaryPrimitives.ReadUInt16LittleEndian(spawn.AsSpan(23)));

        await stream.WriteAsync(ActorPacket(0x0090, actor.ActorId, 8));
        Assert.Equal("[Captain Carocc]\0", Message(await ReadDynamic(stream)));
        Assert.Equal("There are still people in the cabins?!\0", Message(await ReadDynamic(stream)));
        Assert.Equal("At least you are safe.\0", Message(await ReadDynamic(stream)));
        Assert.Equal("Are you alright?\0", Message(await ReadDynamic(stream)));
        Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));

        await stream.WriteAsync(ActorPacket(0x00b9, actor.ActorId, 7));
        var menu = await ReadDynamic(stream);
        Assert.Equal((short)0x00b7, BinaryPrimitives.ReadInt16LittleEndian(menu));
        Assert.Contains("I'm alright, but others need help.:I think I am the last?:\0", Message(menu));

        await stream.WriteAsync(SelectionPacket(actor.ActorId, 1));
        Assert.Equal("[Captain Carocc]\0", Message(await ReadDynamic(stream)));
        Assert.Equal("There are more people left?\0", Message(await ReadDynamic(stream)));
        Assert.Equal("I will send a rescue team to them.\0", Message(await ReadDynamic(stream)));
        Assert.Equal("Thank you for your report.\0", Message(await ReadDynamic(stream)));
        Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));

        // Walk through the remaining pinned "next" boundaries up to the heal/status/quest
        // burst (academy.txt:39-53): 2, 3, 2, 2 messages per boundary, then the final
        // boundary's 2 messages ("[Captain Carocc]" / "It is a hard task...") precede the burst.
        foreach (var messageCount in new[] { 2, 3, 2, 2 })
        {
            await stream.WriteAsync(ActorPacket(0x00b9, actor.ActorId, 7));
            for (var i = 0; i < messageCount; i++) await ReadDynamic(stream);
            Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));
        }

        await stream.WriteAsync(ActorPacket(0x00b9, actor.ActorId, 7));
        Assert.Equal("[Captain Carocc]\0", Message(await ReadDynamic(stream)));
        Assert.Equal("It is a hard task, but you look tough enough.\0", Message(await ReadDynamic(stream)));

        // heal(9999,0): HP 20 -> 40 (clamped to MaxHp), sent via the generic 0x00B0 parameter path.
        var healPacket = await ReadExact(stream, 8);
        Assert.Equal(PacketConstants.ZcParameterChange, BinaryPrimitives.ReadInt16LittleEndian(healPacket));
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(healPacket.AsSpan(2)));
        Assert.Equal(40U, BinaryPrimitives.ReadUInt32LittleEndian(healPacket.AsSpan(4)));
        Assert.Equal(40U, session.GameplayState!.State.CurrentHp);

        // completequest 21001 sends 0x02B4 (quest removed from client log) before getexp's
        // progression packets - matching ai/iro-2026-wire.md's documented completequest
        // wire behavior (removal from the client log, not deletion of server-side state).
        var removeQuest = await ReadExact(stream, 6);
        Assert.Equal((short)0x02b4, BinaryPrimitives.ReadInt16LittleEndian(removeQuest));
        Assert.Equal(21001u, BinaryPrimitives.ReadUInt32LittleEndian(removeQuest.AsSpan(2)));
        Assert.Equal(CharacterQuestStatus.Completed, questPersistence.State);

        // getexp 600,600 through the existing, separately-tested CharacterProgressionService/
        // IroCharacterProgressionPackets path (exact packet sequencing for a given award is
        // covered by CharacterProgressionServiceTests). 600/600 crosses a level threshold from
        // the level-1 fixture, so several parameter packets precede setquest 21008's 0x0B0C -
        // drain until that expected packet ID, requiring at least one progression packet.
        var progressionPacketCount = 0;
        byte[] addQuest;
        while (true)
        {
            var header = await ReadExact(stream, 2);
            var packetId = BinaryPrimitives.ReadInt16LittleEndian(header);
            if (packetId == 0x0b0c) { addQuest = [.. header, .. await ReadExact(stream, IroQuestPackets.AddQuestLength - 2)]; break; }
            var length = packetId == PacketConstants.ZcLongLongParameterChange ? 12 : 8;
            await ReadExact(stream, length - 2);
            progressionPacketCount++;
        }
        Assert.True(progressionPacketCount > 0);
        Assert.Equal(21008u, BinaryPrimitives.ReadUInt32LittleEndian(addQuest.AsSpan(2)));

        // sc_start SC_BLESSING/SC_INCREASEAGI applied to session-local status state; no client packet.
        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out var blessing));
        Assert.Equal(10, blessing.Val1);
        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.IncreaseAgi, out var increaseAgi));
        Assert.Equal(10, increaseAgi.Val1);

        client.Close(); await run.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
    }

    private static byte[] ActorPacket(short type, uint id, int length) { var packet = new byte[length]; BinaryPrimitives.WriteInt16LittleEndian(packet, type); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), id); packet[^1] = 0xaa; return packet; }
    private static byte[] SelectionPacket(uint actorId, byte wireIndex) { var packet = new byte[8]; BinaryPrimitives.WriteInt16LittleEndian(packet, 0x00b8); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId); packet[6] = wireIndex; packet[7] = 0xaa; return packet; }
    private static string Message(byte[] packet) => System.Text.Encoding.ASCII.GetString(packet.AsSpan(8));
    private static async Task<byte[]> ReadDynamic(Stream stream) { var header = await ReadExact(stream, 4); var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)); return [.. header, .. await ReadExact(stream, length - 4)]; }
    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var data = new byte[length];
        await stream.ReadExactlyAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return data;
    }

    // Captain Carocc's script checks two independent quests (21008 for its own switch, 21001
    // for its conditional completequest/getexp), so quest state must be tracked per quest ID.
    private sealed class RecordingQuestPersistence(uint questId, CharacterQuestStatus initialState) : ICharacterQuestPersistence
    {
        private readonly Dictionary<uint, CharacterQuestStatus> _states = new() { [questId] = initialState };
        public CharacterQuestStatus State => _states[questId];
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CancellationToken cancellationToken) =>
            Task.FromResult<CharacterQuestStatus?>(_states.GetValueOrDefault(requestedQuestId, CharacterQuestStatus.Absent));
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CharacterQuestStatus state, CancellationToken cancellationToken)
        {
            _states[requestedQuestId] = state;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        private CharacterGameplayState _state = state;
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(_state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            if (expected.Version != _state.Version) return Task.FromResult<CharacterGameplayState?>(null);
            _state = updated with { Version = expected.Version + 1 };
            return Task.FromResult<CharacterGameplayState?>(_state);
        }
    }
}
