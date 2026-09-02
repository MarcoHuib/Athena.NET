using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;
using Athena.Net.World.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.MapServer.Tests.Net;

// Distributed-World counterpart of GeneratedIntroToIzludeIntegrationTests. That test constructs
// MapClientSession with no distributedWorld, so it only ever exercises the LOCAL fallback movement
// path (MapClientSession.ResolveMovementTarget) - never MovePlayerAsync/TruncateMovementAsync(by
// index)/AdvanceMovementAsync/the real WorldPartitionGrain. This test drives the exact same real
// generated #intro_to_izlude_d OnTouch entity through a REAL Orleans TestCluster-hosted
// IWorldPartitionGrain, backed by the REAL production collision data for int_land04 (via
// RathenaCompatibleMovementPathProvider over the pinned map_cache.dat), so the actual production
// movement boundary (World as collision authority, index-based truncation, timed per-cell advance)
// is what gets proven end-to-end - not just the local fallback.
public sealed class GeneratedIntroToIzludeDistributedIntegrationTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TopologyConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    [Fact]
    public async Task RealRathenaOnTouch_ThroughDistributedWorldAuthority_UsesGeneratedAsyncScript()
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
        // OrleansWorldRuntime is the MapServer-side (client-side) boundary - its own resolver
        // instance decides which partition/grain key a map routes to, independent of whatever
        // resolver the silo's own WorldPartitionGrain instances are constructed with (silo DI is
        // not reachable from _cluster.ServiceProvider, which is the CLIENT'S service provider).
        var resolver = WorldPartitionResolver.CreateDevelopment(["int_land04", "izlude_d"]);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, resolver);
        var gameplayState = new CharacterGameplayState(9, 1, 0, 10, 5, 0, 0, 100, 20, 100, 20, 0, 0, 9, 9, 9, 9, 9, 9);

        await using var session = new MapClientSession(1, serverClient, connector, true,
            gameplayStatePersistence: new FixedGameplayStatePersistence(gameplayState),
            positionPersistence: persistence, questPersistence: persistence, timeProvider: clock,
            worldMapRegistry: registry, distributedWorld: worldRuntime);
        // CompleteIroAuthenticationAsync (not the constructor's mapName/x/y) is what actually
        // establishes _accountId/_charId/_mapName/_x/_y here - the distributed movement/presence
        // boundary requires a real _presenceId, which is ONLY ever set inside EnterPlayerWorldAsync
        // (triggered by 0x007D below), unlike GeneratedIntroToIzludeIntegrationTests' local-fallback
        // sibling test, which needs neither.
        var auth = new MapAuthOkData(7, 9, 1, 2, 0, 0, false, "int_land04", 54, 64, 0, 0, 1, "Fixture",
            HairStyle: 4, HairColor: 2, ClothesColor: 1);
        await session.CompleteIroAuthenticationAsync(auth);
        await ReadExact(stream, 29);
        var bootstrapHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(bootstrapHeader.AsSpan(2)) - 4);
        var run = session.RunAsync(CancellationToken.None);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon
        await ReadExact(stream, 6);  // inventory start
        await ReadExact(stream, 4);  // inventory end
        // Unlike GeneratedIntroToIzludeIntegrationTests' local-fallback sibling (which never sends
        // 0x007D at all, so EnterPlayerWorldAsync/SendVisibleWarpActorsAsync never run - meaning
        // ITS ReadActorId call consumes the NPC's own actor-spawn packet, deferred until arrival),
        // this test genuinely drives CzNotifyActorInit to get a real _presenceId, which means the
        // real #intro_to_izlude_d NPC actor gets sent here at ordinary map-load time instead -
        // capture its actorId from THIS packet (same 9-byte header shape ReadActorId assumes:
        // IroWorldActorPackets.BuildWorldActor's [opcode:2][length:2][objectType:1][actorId:4]).
        var npcActorHeader = await ReadExact(stream, 9);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(npcActorHeader.AsSpan(5));
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(npcActorHeader.AsSpan(2)) - 9);

        var registrationGenerationBeforeMovement = clock.RegistrationGeneration;
        await stream.WriteAsync(BuildMovementPacket(49, 57));
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 12)));

        // Same deterministic clock-driven advance as the local-path sibling test - the walk here is
        // driven through the real MovePlayerAsync -> (index-based) TruncateMovementAsync ->
        // AdvanceMovementAsync boundary against a real WorldPartitionGrain, not the local fallback.
        await MovementSchedulerTestHelpers.AdvanceEntireWalkAsync(
            clock,
            registrationGenerationBeforeMovement,
            cellCount: 64);

        // The NPC actor is already visible (sent above at map-load) - SendVisibleWarpActorsAsync's
        // second call (from ProcessDueMovementAsync's arrival dispatch) is a no-op here, unlike the
        // local-fallback sibling test where THAT call is the actor's first-ever send. So no second
        // ReadActorId call here - the dialogue starts immediately with the first 0x00B4 message.
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
        Assert.Equal(("izlude_d", (ushort)196, (ushort)209), persistence.Position);
        Assert.Equal(("izlude_d", (ushort)128, (ushort)142), persistence.SavePoint);

        client.Close(); await run.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
    }

    // OrleansWorldRuntime.CancelMovementAsync must honor CancellationToken exactly like every other
    // existing boundary method (RegisterPresenceAsync/MovePlayerAsync/TruncateMovementAsync/
    // AdvanceMovementAsync/TransferPlayerAsync/UnregisterPresenceAsync all chain
    // .WaitAsync(cancellationToken) - confirmed by direct inspection of the current file).
    [Fact]
    public async Task OrleansWorldRuntime_CancelMovementAsync_HonorsCancellationToken()
    {
        var resolver = WorldPartitionResolver.CreateDevelopment(["int_land04", "izlude_d"]);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, resolver);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worldRuntime.CancelMovementAsync(
            new WorldMovementCancellation(Guid.NewGuid(), Guid.NewGuid(), 1, "int_land04"), cts.Token));
    }

    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.Services
            .AddSingleton<IWorldPartitionResolver>(WorldPartitionResolver.CreateDevelopment(["int_land04", "izlude_d"]))
            .AddSingleton<IMapCollisionProvider>(_ => MapCollisionStartupLoader.Load(
                [], System.IO.Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat"),
                Athena.Net.MapServer.Gameplay.Rules.RagnarokRuleSet.Renewal))
            .AddSingleton<IMovementPathProvider, RathenaCompatibleMovementPathProvider>();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
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

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    private static async Task WaitUntilAsync(Func<bool> condition) { for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(5); Assert.True(condition()); }
    private static void AssertQuestRemove(uint questId, byte[] packet) { Assert.Equal((short)0x02b4, BinaryPrimitives.ReadInt16LittleEndian(packet)); Assert.Equal(questId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2))); }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private sealed class RecordingPersistence : ICharacterQuestPersistence, ICharacterPositionPersistence
    {
        public Dictionary<uint, CharacterQuestStatus> Quests { get; } = [];
        public (string Map, ushort X, ushort Y) Position { get; private set; }
        public (string Map, ushort X, ushort Y) SavePoint { get; private set; }
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint questId, CancellationToken cancellationToken) => Task.FromResult<CharacterQuestStatus?>(Quests.GetValueOrDefault(questId, CharacterQuestStatus.Absent));
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus state, CancellationToken cancellationToken) { Quests[questId] = state; return Task.FromResult(true); }
        public Task<bool> SavePositionAsync(uint accountId, uint charId, string map, ushort x, ushort y, CancellationToken cancellationToken) { Position = (map, x, y); return Task.FromResult(true); }
        public Task<bool> SavePointAsync(uint accountId, uint charId, string map, ushort x, ushort y, CancellationToken cancellationToken) { SavePoint = (map, x, y); return Task.FromResult(true); }
    }
}
