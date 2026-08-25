using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class GeneratedIntroToIzludeIntegrationTests
{
    [Fact]
    public async Task RealRathenaOnTouch_UsesGeneratedAsyncScriptAndExistingQuestPersistence()
    {
        var entity = Assert.Single(GeneratedScriptRegistry.Entities, item => item.Id == "warp:int_land04:intro_to_izlude_d");
        var registry = new WorldMapRegistry([], [entity]);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync(); await connect;
        await using var stream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var persistence = new RecordingPersistence();
        persistence.Quests[21008] = CharacterQuestStatus.Active;
        persistence.Quests[21001] = CharacterQuestStatus.Active;
        var clock = new ControllableTimeProvider();
        await using var session = new MapClientSession(1, serverClient, connector, true, "int_land04", 54, 64, registry,
            positionPersistence: persistence, questPersistence: persistence, accountId: 7, charId: 9, timeProvider: clock);
        var run = session.RunAsync(CancellationToken.None);

        await stream.WriteAsync(BuildMovementPacket(49, 57));
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 12)));

        // The movement request registered a deferred OnTouch arrival at (50,59) - not fired yet
        // (this is the intersected/truncated destination, before the walk actually reaches it; see
        // MapClientSession.ProcessDueMovementAsync's own doc comment on why arrival is deferred to
        // real elapsed walking time rather than firing at click time). Drive _movementLoop
        // deterministically, one cell-duration step at a time, via the injected fake clock instead
        // of a real-time sleep, until the character's authoritative position actually reaches the
        // trigger cell - mirroring exactly what the scheduler requires in production, just without
        // real wall-clock waiting. Only once the walk actually reaches that cell does
        // ProcessDueMovementAsync's PendingScriptTouchArrival branch send the NPC actor spawn
        // (SendVisibleWarpActorsAsync) and start the OnTouch script - so this must run before the
        // reads below, not after them.
        await MovementSchedulerTestHelpers.AdvanceUntilArrivedAsync(session, clock, client, targetX: 50, targetY: 59);

        var actorId = await ReadActorId(stream);
        Assert.Equal("^4d4dffOnce you leave this island there is no way back.\0", ReadMessage(await ReadDynamic(stream)));
        Assert.Equal("Are you sure you want to go directly to Izlude?^000000\0", ReadMessage(await ReadDynamic(stream)));
        Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));
        Assert.Null(session.ActiveScriptState);
        Assert.Equal(entity.Id, session.ActiveGeneratedScriptEntityId);

        await stream.WriteAsync(BuildActorPacket(0x00b9, actorId, 7));
        Assert.Equal("^4d4dffIf you do, the quest will be deleted from your Quest Log.^000000\0", ReadMessage(await ReadDynamic(stream)));
        Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));

        await stream.WriteAsync(BuildActorPacket(0x00b9, actorId, 7));
        var menu = await ReadDynamic(stream);
        Assert.Equal((short)0x00b7, BinaryPrimitives.ReadInt16LittleEndian(menu));
        Assert.Equal("Do not go to Izlude yet:Sail to Izlude!:\0", ReadMessage(menu));

        await stream.WriteAsync(BuildSelectionPacket(actorId, 2));
        AssertQuestRemove(21008, await ReadExact(stream, 6));
        Assert.Equal("[Sailor]\0", ReadMessage(await ReadDynamic(stream)));
        Assert.Equal("Let's head towards Izlude!\0", ReadMessage(await ReadDynamic(stream)));
        Assert.Equal((short)0x00b6, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));
        await stream.WriteAsync(BuildActorPacket(0x0146, actorId, 7));
        AssertQuestRemove(21001, await ReadExact(stream, 6));
        var mapChange = await ReadExact(stream, 22);
        Assert.Equal((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(mapChange));
        Assert.Equal("izlude_d.gat", System.Text.Encoding.ASCII.GetString(mapChange.AsSpan(2, 16)).TrimEnd('\0'));

        await WaitUntilAsync(() => session.ActiveGeneratedScriptEntityId is null);
        Assert.Null(session.ActiveScriptState);
        Assert.Null(session.ActiveGeneratedScriptEntityId);
        Assert.Equal(CharacterQuestStatus.Completed, persistence.Quests[21008]);
        Assert.Equal(CharacterQuestStatus.Completed, persistence.Quests[21001]);
        Assert.Equal(("izlude_d", (ushort)128, (ushort)142), persistence.SavePoint);

        client.Close(); await run.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
    }

    private static byte[] BuildMovementPacket(ushort x, ushort y)
    {
        var packet = new byte[6]; BinaryPrimitives.WriteInt16LittleEndian(packet, 0x035f);
        packet[2] = (byte)(x >> 2); packet[3] = (byte)((x << 6) | ((y >> 4) & 0x3f)); packet[4] = (byte)(y << 4); packet[5] = 0xaa; return packet;
    }
    private static byte[] BuildActorPacket(short type, uint actorId, int length) { var packet = new byte[length]; BinaryPrimitives.WriteInt16LittleEndian(packet, type); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId); packet[^1] = 0xaa; return packet; }
    private static byte[] BuildSelectionPacket(uint actorId, byte wireIndex) { var packet = BuildActorPacket(0x00b8, actorId, 8); packet[6] = wireIndex; return packet; }
    private static string ReadMessage(byte[] packet) => System.Text.Encoding.ASCII.GetString(packet.AsSpan(8));
    private static async Task<byte[]> ReadDynamic(Stream stream) { var header = await ReadExact(stream, 4); var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)); return [.. header, .. await ReadExact(stream, length - 4)]; }
    private static async Task<uint> ReadActorId(Stream stream) { var header = await ReadExact(stream, 9); var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5)); await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) - 9); return id; }

    // Bounded assertion read: a 10-second guard against a packet that never arrives (e.g. a
    // scheduler regression like the one that originally hung this exact test), NOT a
    // synchronization mechanism - every await in this file that reaches the network goes through
    // this method so a missing packet fails the test instead of hanging the whole CI run.
    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    private static async Task WaitUntilAsync(Func<bool> condition) { for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(5); Assert.True(condition()); }
    private static void AssertQuestRemove(uint questId, byte[] packet) { Assert.Equal((short)0x02b4, BinaryPrimitives.ReadInt16LittleEndian(packet)); Assert.Equal(questId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2))); }

    private sealed class RecordingPersistence : ICharacterQuestPersistence, ICharacterPositionPersistence
    {
        public Dictionary<uint, CharacterQuestStatus> Quests { get; } = [];
        public (string Map, ushort X, ushort Y) SavePoint { get; private set; }
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint questId, CancellationToken cancellationToken) => Task.FromResult<CharacterQuestStatus?>(Quests.GetValueOrDefault(questId, CharacterQuestStatus.Absent));
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus state, CancellationToken cancellationToken) { Quests[questId] = state; return Task.FromResult(true); }
        public Task<bool> SavePositionAsync(uint accountId, uint charId, string map, ushort x, ushort y, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> SavePointAsync(uint accountId, uint charId, string map, ushort x, ushort y, CancellationToken cancellationToken) { SavePoint = (map, x, y); return Task.FromResult(true); }
    }
}
