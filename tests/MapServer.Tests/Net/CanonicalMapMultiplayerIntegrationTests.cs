using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.MapServer.Tests.Net;

// Channel removal, end to end (ai/map-server.md "Canonical maps"): pinned rAthena's parallel copy maps
// (prt_fild08a..d ...) are canonicalized onto ONE shared map. Two REAL authenticated MapClientSessions -
// one whose persisted location is the canonical prt_fild08, one whose persisted location is the legacy
// copy prt_fild08c - run against a REAL Orleans-hosted World partition grain (never a scripted fake) and
// must end up in the same simulation: same map, mutual visibility and movement, the SAME authoritative
// monster actor ids, and shared HP/death state. No per-map snapshot is faked anywhere.
public sealed class CanonicalMapMultiplayerIntegrationTests : IAsyncLifetime
{
    private const string CanonicalMap = "prt_fild08";
    private const string LegacyCopy = "prt_fild08c";
    private const ushort MonsterX = 100;
    private const ushort MonsterY = 100;
    private const uint AccountA = 2_000_050;
    private const uint AccountB = 2_000_000;

    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TopologyConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private static IWorldPartitionResolver Resolver() => WorldPartitionTopologyLoader.Load(Path.Combine(FindRepositoryRoot(), "conf", "world_partitions.json"), [CanonicalMap]);

    private static MapServerWorld MakeWorld() => new(
        WorldMapRegistry.Tutorial,
        [new MobSpawnDefinition(GeneratedMobRegistry.Get(1002), CanonicalMap, 1, RespawnDelay: 60_000, RespawnRandomDelay: 0,
            new WorldSourceInfo("rAthena", "abc", "test", 0), SpawnName: "Poring", X: (short)MonsterX, Y: (short)MonsterY, Xs: 1, Ys: 1)],
        new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules()),
        EmptyMapCollisionProvider.Instance, new UnverifiedGridLineMovementPathProvider(), new MonsterFeedProjectionRegistry(), new MonsterAttackCadenceStore());

    private sealed record Player(MapClientSession Session, TcpClient Client, NetworkStream Stream, Task Run);

    // `persistedMap` is the map the persisted location names (what CharServer's MapAuthNode carries): for
    // the legacy copy this is the ALIAS, and the session itself is responsible for landing on the canonical map.
    private static async Task<Player> ConnectAsync(MapServerWorld world, IWorldRuntime runtime, uint accountId, string persistedMap, ushort x, ushort y)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();
        var state = new CharacterGameplayState(accountId, 1, 0, 99, 10, 0, 0, 100, 20, 100, 20, 0, 0, 99, 9, 9, 9, 99, 9);
        var session = new MapClientSession(
            (int)accountId, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), iroAuthenticated: true,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            monsterProjections: world.MonsterProjections, combat: world.Combat, combatState: world.CombatState,
            movementPathProvider: world.MovementPathProvider, collisionProvider: world.Collision,
            players: world.Players, playerVisibility: world.PlayerVisibility, visibilityOptions: world.Visibility,
            distributedWorld: runtime);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new MapAuthOkData(accountId, accountId, 1, 2, 0, 0, false, persistedMap, x, y, 0, 0, 1, $"P{accountId}", HairStyle: 4, HairColor: 2, ClothesColor: 1));
        await ReadExact(stream, 29);
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon
        await ReadExact(stream, 6);  // inventory start
        await ReadExact(stream, 4);  // inventory end
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(session.IsWorldMapEligible);
        return new Player(session, client, stream, run);
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    // Reads whole packets (fixed-size table for the opcodes this scenario can interleave, otherwise the
    // length-prefixed shape) until `match` accepts one - so an unrelated idle monster/NPC packet can never
    // desynchronize the framing.
    private static async Task<byte[]> ReadUntilAsync(Stream stream, Func<byte[], bool> match)
    {
        while (true)
        {
            var opcodeBytes = await ReadExact(stream, 2);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(opcodeBytes);
            byte[] packet;
            var fixedLength = opcode == 0x0087 ? 12 : opcode == 0x0088 ? 10 : MapTcpServerMonsterAuthorityIntegrationTests.KnownFixedPacketLength(opcode);
            if (fixedLength >= 0) packet = [.. opcodeBytes, .. await ReadExact(stream, fixedLength - 2)];
            else
            {
                var lengthField = await ReadExact(stream, 2);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthField);
                packet = [.. opcodeBytes, .. lengthField, .. await ReadExact(stream, length - 4)];
            }
            if (match(packet)) return packet;
        }
    }

    private static bool IsPlayerIntro(byte[] packet, uint actorId) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) is 0x09fe or 0x09ff && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == actorId && packet[4] == 0;

    private static bool IsMonsterIntro(byte[] packet) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyStandEntry && packet[4] == 5;

    private static byte[] BuildMovementRequest(ushort x, ushort y)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x035f);
        packet[2] = (byte)(x >> 2);
        packet[3] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        packet[4] = (byte)(y << 4);
        packet[5] = 0x44;
        return packet;
    }

    private static byte[] BuildAttackPacket(uint targetActorId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x0437);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), targetActorId);
        packet[6] = 7; // DMG_REPEAT
        packet[7] = 0x7F;
        return packet;
    }

    [Fact]
    public async Task PersistedCanonicalAndLegacyCopy_LandOnOneSharedMap_WithSharedMonstersMovementAndDeathState()
    {
        var world = MakeWorld();
        var runtime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, runtime);

        var a = await ConnectAsync(world, runtime, AccountA, persistedMap: CanonicalMap, x: 99, y: 100);
        var b = await ConnectAsync(world, runtime, AccountB, persistedMap: LegacyCopy, x: 101, y: 100);
        using var clientA = a.Client;
        using var clientB = b.Client;

        // One map: the legacy-copy character was canonicalized at the session boundary, same coordinates.
        Assert.Equal(CanonicalMap, a.Session.CurrentMapName);
        Assert.Equal(CanonicalMap, b.Session.CurrentMapName);
        Assert.Single(new[] { a.Session, b.Session }.Select(session => session.CurrentMapName).Distinct(StringComparer.OrdinalIgnoreCase));
        // Item 14 §4: local PlayerPresenceRegistry membership is feed-driven only, never a direct
        // side effect of World registration - drive a real player-feed tick before asserting it.
        await server.ProcessOnePlayerTickAsync([a.Session, b.Session], CancellationToken.None);
        Assert.True(world.Players.TryGetByActorId(AccountA, out var presenceA));
        Assert.True(world.Players.TryGetByActorId(AccountB, out var presenceB));
        Assert.Equal(CanonicalMap, presenceA.MapName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CanonicalMap, presenceB.MapName, StringComparer.OrdinalIgnoreCase); // Not prt_fild08c: one visibility domain.

        // Mutual visibility through the unchanged PlayerVisibilityCoordinator.
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, AccountB));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, AccountA));

        // ONE monster tick over BOTH sessions: one map group, one projection, the SAME authoritative actor ids.
        await server.ProcessOneMonsterTickAsync([a.Session, b.Session], CancellationToken.None); // SpawnInitializationRequired -> load.
        await server.ProcessOneMonsterTickAsync([a.Session, b.Session], CancellationToken.None); // Bootstrap.
        await server.ProcessOneMonsterTickAsync([a.Session, b.Session], CancellationToken.None); // Discovery fan-out.
        Assert.True(world.MonsterProjections.TryGet(CanonicalMap, out var projection));
        Assert.False(world.MonsterProjections.TryGet(LegacyCopy, out _)); // No sibling-copy simulation was ever created.
        var monster = Assert.Single(projection.AllInstances);
        var introA = await ReadUntilAsync(a.Stream, IsMonsterIntro);
        var introB = await ReadUntilAsync(b.Stream, IsMonsterIntro);
        Assert.Equal(monster.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(introA.AsSpan(5)));
        Assert.Equal(monster.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(introB.AsSpan(5)));

        // Movement is observable both ways (A -> B, then B -> A). Item 14 §3/§4: the observer's own
        // movement broadcast (0x09FD) is feed-driven, via ConfirmMovementProjectionAsync's own
        // MovementStarted entry - a player-feed tick (folded into ProcessOneMonsterTickAsync, which
        // also polls the player feed per map group) is required after each movement before the
        // OTHER session's stream carries it.
        await a.Stream.WriteAsync(BuildMovementRequest(99, 101));
        await ReadUntilAsync(a.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x0087);
        await server.ProcessOneMonsterTickAsync([a.Session, b.Session], CancellationToken.None);
        var aWalk = await ReadUntilAsync(b.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x09fd && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == AccountA);
        Assert.Equal(AccountA, BinaryPrimitives.ReadUInt32LittleEndian(aWalk.AsSpan(5)));
        await b.Stream.WriteAsync(BuildMovementRequest(101, 101));
        await ReadUntilAsync(b.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x0087);
        await server.ProcessOneMonsterTickAsync([a.Session, b.Session], CancellationToken.None);
        var bWalk = await ReadUntilAsync(a.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x09fd && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == AccountB);
        Assert.Equal(AccountB, BinaryPrimitives.ReadUInt32LittleEndian(bWalk.AsSpan(5)));

        // Shared HP/death: A kills the monster; World (the single authority) reports the life Dead, and B -
        // who never attacked - observes the SAME actor's death through the shared feed.
        await a.Stream.WriteAsync(BuildAttackPacket(monster.ActorId));
        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(CanonicalMap));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var confirmedDead = false;
        while (DateTime.UtcNow < deadline && !confirmedDead)
        {
            await Task.Delay(100);
            var page = await grain.PollMonsterFeedAsync(cursor: null, CanonicalMap);
            confirmedDead = page.Snapshot!.SingleOrDefault(instance => instance.ActorId == monster.ActorId) is { Lifecycle: WorldMonsterLifecycleState.Dead };
        }
        Assert.True(confirmedDead, "Expected World's authoritative state to show the monster killed by player A.");

        var vanishB = ReadUntilAsync(b.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyVanish && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)) == monster.ActorId);
        var vanishDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < vanishDeadline && !vanishB.IsCompleted)
        {
            await server.ProcessOneMonsterTickAsync([a.Session, b.Session], CancellationToken.None);
            await Task.Delay(20);
        }
        var vanish = await vanishB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanish[6]);
        Assert.Equal(WorldMonsterLifecycleState.Dead, projection.AllInstances.Single(instance => instance.ActorId == monster.ActorId).Lifecycle);

        clientA.Close(); clientB.Close();
        await Task.WhenAll(a.Run.WaitAsync(TimeSpan.FromSeconds(5)), b.Run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services
                .AddSingleton<IWorldPartitionResolver>(Resolver())
                .AddSingleton<IMovementPathProvider>(new UnverifiedGridLineMovementPathProvider())
                .AddSingleton<IMapCollisionProvider>(new MapCollisionProvider([new MapCollisionMap(CanonicalMap, 200, 200, Enumerable.Repeat(MapCellFlags.Walkable, 200 * 200).ToArray())]))
                .AddSingleton(TimeProvider.System);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }
}
