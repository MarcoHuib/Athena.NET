using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class GeneratedLuminIntegrationTests
{
    private const string EntityId = "npc:int_land03:lumin#new_ship03";
    private const string AuthoritativeCharacterName = "ServerOwnedHero";

    [Fact]
    public async Task CompletedQuestBranch_UsesAuthoritativeNameMenuCloakAndClose2Continuation()
    {
        var persistence = new RecordingQuestPersistence(CharacterQuestStatus.Completed);
        await using var fixture = await LuminFixture.StartAsync(persistence);

        await fixture.LoadAndAssertSpawnAsync();
        await fixture.Stream.WriteAsync(ActorPacket(0x0090, fixture.ActorId, 8));
        AssertCutin(await ReadExact(fixture.Stream, 67), "nov_lumin01.bmp", 0);
        await AssertMessagesAsync(fixture.Stream, "[Lumin]\0", ".....\0");
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        var menu = await ReadDynamic(fixture.Stream);
        Assert.Equal((short)0x00b7, BinaryPrimitives.ReadInt16LittleEndian(menu));
        Assert.Equal("Should I introduce myself?:My name is ~!:\0", Message(menu));

        await fixture.Stream.WriteAsync(SelectionPacket(fixture.ActorId, 2));
        await AssertMessagesAsync(fixture.Stream, $"[{AuthoritativeCharacterName}]\0", $"I am {AuthoritativeCharacterName}!\0");
        AssertNext(await ReadExact(fixture.Stream, 6));

        await ResumeAndAssertMessagesAsync(fixture, "[Lu]\0", ".....\0");
        await ResumeAndAssertMessagesAsync(fixture, "[Lu]\0", ".....\0", "....So?\0");

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        AssertCloak(await ReadExact(fixture.Stream, 15), fixture.ActorId);
        await AssertMessagesAsync(fixture.Stream, "- Lu just walked away with a cynical look on his face.\0");
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        AssertCutin(await ReadExact(fixture.Stream, 67), "fly_trock.bmp", 2);
        await AssertMessagesAsync(fixture.Stream, "[Captain Carocc]\0", "Looks like... he has a shy personality.\0", "When you go to Izlude, will you check on him?\0");
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        await AssertMessagesAsync(fixture.Stream, "[Captain Carocc]\0", "From now on I'll be sailing the ship around the island.\0", "Let's go, shall we?\0");
        AssertClose(await ReadExact(fixture.Stream, 6));
        await fixture.Stream.WriteAsync(ActorPacket(0x0146, fixture.ActorId, 7));
        AssertCutin(await ReadExact(fixture.Stream, 67), "", 255);
        await fixture.WaitForScriptCompletionAsync();

        Assert.Empty(persistence.Mutations);
    }

    [Fact]
    public async Task AbsentQuestBranch_PersistsQuest7471ThenCloaksAndTerminatesWithoutFallthrough()
    {
        var persistence = new RecordingQuestPersistence(CharacterQuestStatus.Absent);
        await using var fixture = await LuminFixture.StartAsync(persistence);

        await fixture.LoadAndAssertSpawnAsync();
        await fixture.Stream.WriteAsync(ActorPacket(0x0090, fixture.ActorId, 8));
        AssertCutin(await ReadExact(fixture.Stream, 67), "nov_lumin01.bmp", 0);
        await AssertMessagesAsync(fixture.Stream, "[Lumin]\0", ".............\0", "..?\0");
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        AssertCutin(await ReadExact(fixture.Stream, 67), "fly_trock.bmp", 2);
        await AssertMessagesAsync(fixture.Stream, "[Captain Carocc]\0", "Had a good dream?\0", "Soon, we will get to Izlude. And you can talk to other people like you just talked to me.\0");
        AssertNext(await ReadExact(fixture.Stream, 6));

        foreach (var messageCount in new[] { 3, 3, 3, 3, 2 })
            await ResumeAndDrainBoundaryAsync(fixture, messageCount);

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        await AssertMessagesAsync(fixture.Stream, "[Captain Carocc]\0", "To get off this ship, you should enter the ^4d4dffShining Portal^000000 over there.\0", "All transportation is made through the portals.\0");
        var addQuest = await ReadExact(fixture.Stream, IroQuestPackets.AddQuestLength);
        Assert.Equal((short)0x0b0c, BinaryPrimitives.ReadInt16LittleEndian(addQuest));
        Assert.Equal(7471u, BinaryPrimitives.ReadUInt32LittleEndian(addQuest.AsSpan(2)));
        var removeQuest = await ReadExact(fixture.Stream, 6);
        Assert.Equal((short)0x02b4, BinaryPrimitives.ReadInt16LittleEndian(removeQuest));
        Assert.Equal(7471u, BinaryPrimitives.ReadUInt32LittleEndian(removeQuest.AsSpan(2)));
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        AssertCutin(await ReadExact(fixture.Stream, 67), "nov_lumin01.bmp", 0);
        await AssertMessagesAsync(fixture.Stream, "[Lumin]\0", "Yes.\0");
        AssertNext(await ReadExact(fixture.Stream, 6));

        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        AssertCloak(await ReadExact(fixture.Stream, 15), fixture.ActorId);
        AssertCutin(await ReadExact(fixture.Stream, 67), "fly_trock.bmp", 2);
        await AssertMessagesAsync(fixture.Stream, "[Captain Carocc]\0", "Oh boy.\0", "What a cute reaction.\0");
        AssertClose(await ReadExact(fixture.Stream, 6));
        await fixture.Stream.WriteAsync(ActorPacket(0x0146, fixture.ActorId, 7));
        AssertCutin(await ReadExact(fixture.Stream, 67), "", 255);
        await fixture.WaitForScriptCompletionAsync();

        Assert.Equal([CharacterQuestStatus.Active, CharacterQuestStatus.Completed], persistence.Mutations);
        Assert.Equal(CharacterQuestStatus.Completed, persistence.State);
    }

    private static async Task ResumeAndAssertMessagesAsync(LuminFixture fixture, params string[] messages)
    {
        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        await AssertMessagesAsync(fixture.Stream, messages);
        AssertNext(await ReadExact(fixture.Stream, 6));
    }

    private static async Task ResumeAndDrainBoundaryAsync(LuminFixture fixture, int messageCount)
    {
        await fixture.Stream.WriteAsync(ActorPacket(0x00b9, fixture.ActorId, 7));
        for (var index = 0; index < messageCount; index++) await ReadDynamic(fixture.Stream);
        AssertNext(await ReadExact(fixture.Stream, 6));
    }

    private static async Task AssertMessagesAsync(Stream stream, params string[] expected)
    {
        foreach (var message in expected) Assert.Equal(message, Message(await ReadDynamic(stream)));
    }

    private static void AssertCutin(byte[] packet, string image, byte position)
    {
        Assert.Equal((short)0x01b3, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(image, System.Text.Encoding.ASCII.GetString(packet.AsSpan(2, 64)).TrimEnd('\0'));
        Assert.Equal(position, packet[66]);
    }

    private static void AssertCloak(byte[] packet, uint actorId)
    {
        Assert.Equal((short)0x0229, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10)));
    }

    private static void AssertNext(byte[] packet) => Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(packet));
    private static void AssertClose(byte[] packet) => Assert.Equal((short)0x00b6, BinaryPrimitives.ReadInt16LittleEndian(packet));
    private static string Message(byte[] packet) => System.Text.Encoding.ASCII.GetString(packet.AsSpan(8));
    private static byte[] ActorPacket(short type, uint id, int length) { var packet = new byte[length]; BinaryPrimitives.WriteInt16LittleEndian(packet, type); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), id); packet[^1] = 0xaa; return packet; }
    private static byte[] SelectionPacket(uint actorId, byte wireIndex) { var packet = ActorPacket(0x00b8, actorId, 8); packet[6] = wireIndex; return packet; }
    private static async Task<byte[]> ReadDynamic(Stream stream) { var header = await ReadExact(stream, 4); var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)); return [.. header, .. await ReadExact(stream, length - 4)]; }
    private static async Task<byte[]> ReadExact(Stream stream, int length) { var data = new byte[length]; await stream.ReadExactlyAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); return data; }

    private sealed class RecordingQuestPersistence(CharacterQuestStatus initialState) : ICharacterQuestPersistence
    {
        public CharacterQuestStatus State { get; private set; } = initialState;
        public List<CharacterQuestStatus> Mutations { get; } = [];
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint questId, CancellationToken cancellationToken) => Task.FromResult<CharacterQuestStatus?>(State);
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus state, CancellationToken cancellationToken)
        {
            Assert.Equal(7471u, questId);
            State = state;
            Mutations.Add(state);
            return Task.FromResult(true);
        }
    }

    private sealed class LuminFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly MapClientSession _session;
        private readonly Task _run;

        private LuminFixture(TcpListener listener, TcpClient client, MapClientSession session, Task run, NetworkStream stream, uint actorId)
        {
            _listener = listener; _client = client; _session = session; _run = run; Stream = stream; ActorId = actorId;
        }

        public NetworkStream Stream { get; }
        public uint ActorId { get; }

        public static async Task<LuminFixture> StartAsync(ICharacterQuestPersistence persistence)
        {
            var entity = Assert.Single(GeneratedScriptRegistry.Entities, item => item.Id == EntityId);
            Assert.Equal(new WorldActorComponent("Lumin#new_ship03", "int_land03", 73, 100, 3, 639, 0), entity.Actor);
            var registry = new WorldMapRegistry([], [entity]);
            var actor = Assert.Single(registry.GetVisibleWarpActors("int_land03", 73, 100));
            Assert.True(registry.TryGetInteraction(actor.ActorId, "int_land03", out _, out _));

            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var serverClient = await listener.AcceptTcpClientAsync(); await connect;
            var session = new MapClientSession(1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true, "int_land03", 73, 100, registry,
                questPersistence: persistence, accountId: 7, charId: 9, gameplayStatePersistence: new FixedGameplayStatePersistence());
            var run = session.RunAsync(CancellationToken.None);
            await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "int_land03", 73, 100, 3, 0, 0, AuthoritativeCharacterName));
            var stream = client.GetStream();
            await ReadExact(stream, 29); // authenticated iRO bootstrap
            return new(listener, client, session, run, stream, actor.ActorId);
        }

        public async Task LoadAndAssertSpawnAsync()
        {
            await Stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
            await ReadExact(Stream, 15); // self weapon appearance
            await ReadExact(Stream, 6); // empty inventory start
            await ReadExact(Stream, 4); // inventory end
            var spawn = await ReadDynamic(Stream);
            Assert.Equal(ActorId, BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5)));
            Assert.Equal((ushort)639, BinaryPrimitives.ReadUInt16LittleEndian(spawn.AsSpan(23)));
        }

        public async Task WaitForScriptCompletionAsync()
        {
            for (var attempt = 0; attempt < 100 && _session.ActiveGeneratedScriptEntityId is not null; attempt++) await Task.Delay(10);
            Assert.Null(_session.ActiveGeneratedScriptEntityId);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Close();
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
            await _session.DisposeAsync();
            _listener.Stop();
        }
    }

    private sealed class FixedGameplayStatePersistence : ICharacterGameplayStatePersistence
    {
        private static readonly CharacterGameplayState State = new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(State);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(null);
    }
}
