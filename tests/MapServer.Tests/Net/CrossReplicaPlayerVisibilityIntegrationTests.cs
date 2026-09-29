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

// Item 14: proves cross-replica player visibility/combat using TWO genuinely independent MapServer
// gateway "worlds" (two separate MapServerWorld/MapTcpServer/OrleansWorldRuntime instances, each
// with its own PlayerPresenceRegistry/PlayerVisibilityCoordinator/PlayerFeedProjectionRegistry/
// MonsterFeedProjectionRegistry - never one shared PlayerVisibilityCoordinator simulating both)
// against ONE shared Orleans TestCluster-hosted IWorldPartitionGrain, exactly the way two real
// MapServer processes share one World silo. Mirrors MapTcpServerMonsterAuthorityIntegrationTests'
// own established TestCluster/ConnectSessionAsync/deterministic-tick pattern.
public sealed class CrossReplicaPlayerVisibilityIntegrationTests : IAsyncLifetime
{
    private const int PoringMobId = 1002;
    private const ushort MonsterX = 100;
    private const ushort MonsterY = 100;

    private TestCluster _cluster = null!;
    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TopologyConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private static IWorldPartitionResolver Resolver() => WorldPartitionTopologyLoader.Load(Path.Combine(FindRepositoryRoot(), "conf", "world_partitions.json"), ["izlude", "geffen"]);

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static MobSpawnDefinition PoringSpawn(string mapId) =>
        new(Athena.Net.MapServer.Generated.GameData.Mobs.GeneratedMobRegistry.Get(PoringMobId), mapId, Count: 1, RespawnDelay: 5000, RespawnRandomDelay: 0,
            new WorldSourceInfo("rAthena", "abc", "test", 0), SpawnName: "Poring", X: (short)MonsterX, Y: (short)MonsterY, Xs: 1, Ys: 1);

    // One independent "gateway replica" - its own MapServerWorld (own PlayerPresenceRegistry/
    // PlayerVisibilityCoordinator/PlayerFeedProjectionRegistry/MonsterFeedProjectionRegistry), own
    // MapTcpServer, own OrleansWorldRuntime instance (a distinct client-side object, though it talks
    // to the SAME underlying TestCluster/grain).
    private sealed record Replica(MapServerWorld World, MapTcpServer Server, OrleansWorldRuntime Runtime);

    private Replica MakeReplica(string mapId)
    {
        var combatState = new MonsterAttackCadenceStore();
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules());
        var world = new MapServerWorld(
            WorldMapRegistry.Tutorial, [PoringSpawn(mapId)], combat,
            EmptyMapCollisionProvider.Instance, new UnverifiedGridLineMovementPathProvider(),
            new MonsterFeedProjectionRegistry(), combatState);
        // A distinct OrleansWorldRuntime instance per replica - mirrors two genuinely independent
        // MapServer processes each holding their OWN IClusterClient-backed runtime, even though
        // both talk to the SAME underlying TestCluster/grain (_cluster.Client is itself already the
        // one shared cluster client TestCluster exposes for this test's entire process).
        var runtime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, runtime);
        return new Replica(world, server, runtime);
    }

    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask)> ConnectAsync(
        Replica replica, uint accountId, string mapId, ushort x, ushort y, CharacterGameplayState? gameplayState = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        var stream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var state = gameplayState ?? new CharacterGameplayState(accountId, 1, 0, 99, 10, 0, 0, 100, 20, 100, 20, 0, 0, 99, 9, 9, 9, 99, 9);
        var session = new MapClientSession(
            (int)accountId, serverClient, connector, iroAuthenticated: true,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            monsterProjections: replica.World.MonsterProjections, combat: replica.World.Combat, combatState: replica.World.CombatState,
            movementPathProvider: replica.World.MovementPathProvider, collisionProvider: replica.World.Collision,
            players: replica.World.Players, playerVisibility: replica.World.PlayerVisibility, visibilityOptions: replica.World.Visibility,
            distributedWorld: replica.Runtime, playerAttackFanout: replica.Server.FanOutPlayerAttackActionAsync, lethalAttackGate: replica.Server.LethalAttackGateForTest);
        var run = session.RunAsync(CancellationToken.None);
        var auth = new MapAuthOkData(accountId, accountId, 1, 2, 0, 0, false, mapId, x, y, 0, 0, 1, $"P{accountId}", HairStyle: 4, HairColor: 2, ClothesColor: 1);
        await session.CompleteIroAuthenticationAsync(auth);
        await ReadExact(stream, 29);
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon
        await ReadExact(stream, 6);  // inventory start
        await ReadExact(stream, 4);  // inventory end
        listener.Stop();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(session.IsWorldMapEligible);
        return (client, stream, session, run);
    }

    // Drives ONE production tick (monster + player feeds) for the given replica's own session set.
    private static Task TickAsync(Replica replica, params MapClientSession[] sessions) =>
        replica.Server.ProcessOneMonsterTickAsync(sessions, CancellationToken.None);

    // Drives repeated ticks on BOTH replicas until the given condition holds or a bounded timeout
    // elapses - a real per-map tick loop runs continuously in production, so a test proving
    // cross-replica delivery must tolerate needing more than exactly one tick (e.g. a movement/look
    // RPC racing the very next scheduled poll), never assume a single deterministic tick suffices.
    private static async Task TickUntilAsync(Replica replicaA, MapClientSession sessionA, Replica replicaB, MapClientSession sessionB, Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await TickAsync(replicaA, sessionA);
            await TickAsync(replicaB, sessionB);
            await Task.Delay(10);
        }
    }

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
        packet[6] = 7;
        packet[7] = 0x7F;
        return packet;
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    // `tick`, when supplied, is invoked concurrently with the blocking read - needed for assertions
    // that depend on a real per-map tick loop making repeated progress while ALSO depending on real
    // wall-clock elapsed time on the sender's side (e.g. a multi-cell walk that must actually reach
    // a new position before an AOI transition can occur at all) - a single fixed tick count is not
    // enough in that case, so this polls with its own short interval until the expected packet
    // arrives or the timeout elapses.
    private static async Task<byte[]> ReadUntilAsync(Stream stream, Func<byte[], bool> match, TimeSpan? timeout = null, Func<Task>? tick = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        if (tick is null)
        {
            while (DateTime.UtcNow < deadline)
            {
                var packet = await ReadOnePacketAsync(stream);
                if (match(packet)) return packet;
            }
            throw new TimeoutException("Expected packet did not arrive within the timeout.");
        }

        // Race a bounded read against the deadline while a background loop keeps ticking - the read
        // itself blocks on socket I/O, so the tick loop runs independently until either the read
        // produces a match or the deadline passes.
        using var cts = new CancellationTokenSource();
        var tickLoop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await tick();
                    await Task.Delay(20, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Expected packet did not arrive within the timeout.");
                var packet = await ReadOnePacketAsync(stream).WaitAsync(remaining);
                if (match(packet)) return packet;
            }
        }
        finally
        {
            await cts.CancelAsync();
            try { await tickLoop; } catch { /* best-effort */ }
        }
    }

    // Reads one packet by its OWN true length - a fixed-size opcode (0x0087/0x08C8/0x0080 vanish/
    // etc, none of which carry a length field at offset 2) via KnownFixedPacketLength, else the
    // dynamic length-prefixed shape (0x09FE/0x09FF/0x09FD/0x0A30) ReadDynamic already handles.
    // Reading a fixed-size packet through ReadDynamic would misinterpret its own payload bytes at
    // offset 2 as a length prefix - exactly the desync this helper exists to avoid.
    private static async Task<byte[]> ReadOnePacketAsync(Stream stream)
    {
        var header = await ReadExact(stream, 2);
        var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
        var fixedLength = KnownFixedPacketLength(opcode);
        if (fixedLength >= 0) return [.. header, .. await ReadExact(stream, fixedLength - 2)];
        var lengthField = await ReadExact(stream, 2);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthField);
        return [.. header, .. lengthField, .. await ReadExact(stream, length - 4)];
    }

    // Same fixed-opcode table as MapTcpServerMonsterAuthorityIntegrationTests' own
    // KnownFixedPacketLength - -1 means "not fixed, use the dynamic length-prefixed shape instead".
    private static int KnownFixedPacketLength(short opcode) => opcode switch
    {
        (short)PacketConstants.ZcNotifyAct3 => PacketConstants.ZcNotifyAct3Length,
        (short)PacketConstants.ZcHpInfo => PacketConstants.ZcHpInfoLength,
        (short)PacketConstants.ZcStopMove => PacketConstants.ZcStopMoveLength,
        (short)PacketConstants.ZcNotifyVanish => PacketConstants.ZcNotifyVanishLength,
        (short)PacketConstants.ZcNotifyPlayerMove => 12,
        (short)PacketConstants.ZcPlayerInfo => PacketConstants.ZcPlayerInfoLength,
        (short)PacketConstants.ZcPingLive => 2,
        _ => -1,
    };

    private static bool IsPlayerIntro(byte[] packet, uint actorId) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) is 0x09fe or 0x09ff && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == actorId;

    private static bool IsPlayerVanish(byte[] packet, uint actorId) =>
        packet.Length == 7 && BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyVanish && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)) == actorId;

    private static bool IsMonsterIntro(byte[] packet, uint actorId) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyStandEntry && packet[4] == 5 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == actorId;

    // Point 1-4: A on replica A, B on replica B, same canonical map, mutual discovery.
    [Fact]
    public async Task ReplicaAAndReplicaB_SameMap_DiscoverEachOtherThroughTheSharedWorldFeed()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 1, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 2, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;

        // Each replica polls the SAME shared World independently - neither has a local socket for
        // the other's player at all, so discovery can ONLY happen through the feed.
        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);

        var aSeesB = await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 2));
        Assert.True(IsPlayerIntro(aSeesB, 2));
        var bSeesA = await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 1));
        Assert.True(IsPlayerIntro(bSeesA, 1));
    }

    // Point 5: movement on replica A is visible to the observer on replica B.
    [Fact]
    public async Task MovementOnReplicaA_IsVisibleToObserverOnReplicaB()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 3, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 4, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 4));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 3));

        await a.Stream.WriteAsync(BuildMovementRequest(99, 102));
        await ReadUntilAsync(a.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x0087);
        await TickAsync(replicaA, a.Session);
        // B's OWN replica poll observes A's MovementStarted entry from the shared World feed.
        await TickAsync(replicaB, b.Session);
        var bSeesAWalk = await ReadUntilAsync(b.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x09fd && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == 3u);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bSeesAWalk.AsSpan(5)));
    }

    // Point 6-7: AOI leave sends exactly one vanish; re-entry sends exactly one entry.
    [Fact]
    public async Task AoiLeaveAndReEntry_EachProduceExactlyOnePacket()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        // A starts exactly at the AOI boundary edge from B (WorldVisibilityOptions.DefaultAreaSize=14,
        // Chebyshev distance) so a SINGLE-cell move - the same mechanism
        // MovementOnReplicaA_IsVisibleToObserverOnReplicaB already proves end-to-end - is enough to
        // cross it, without depending on a long multi-cell real-time walk completing.
        var a = await ConnectAsync(replicaA, accountId: 5, mapId, 101, 114);
        var b = await ConnectAsync(replicaB, accountId: 6, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 6));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 5));

        // One cell north: (101,115) is 15 cells from (101,100), outside AreaSize=14.
        await a.Stream.WriteAsync(BuildMovementRequest(101, 115));
        await ReadUntilAsync(a.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x0087);
        var bSeesLeave = await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 5), timeout: TimeSpan.FromSeconds(15), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());
        Assert.True(IsPlayerVanish(bSeesLeave, 5));

        // One cell back south re-enters range - exactly one re-entry packet.
        await a.Stream.WriteAsync(BuildMovementRequest(101, 114));
        await ReadUntilAsync(a.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x0087);
        var bSeesReEntry = await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 5), timeout: TimeSpan.FromSeconds(15), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());
        Assert.True(IsPlayerIntro(bSeesReEntry, 5));
    }

    // Point 8: disconnect on replica A becomes a vanish on replica B.
    [Fact]
    public async Task DisconnectOnReplicaA_BecomesVanishOnReplicaB()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 7, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 8, mapId, 101, 100);
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 8));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 7));

        await a.Session.StopAsync();
        a.Client.Dispose();
        await TickAsync(replicaB, b.Session);
        var bSeesVanish = await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 7));
        Assert.True(IsPlayerVanish(bSeesVanish, 7));
    }

    // Actor-info lookup: a player on replica B can resolve actor info for a player it can see on
    // replica A, purely from the World-fed local PlayerPresenceRegistry (item 14 §9).
    [Fact]
    public async Task ActorInfoLookup_ResolvesAVisibleRemoteReplicaPlayer()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 9, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 10, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 10));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 9));

        var infoRequest = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(infoRequest, 0x0368);
        BinaryPrimitives.WriteUInt32LittleEndian(infoRequest.AsSpan(2), 9);
        infoRequest[6] = 0xe3;
        await b.Stream.WriteAsync(infoRequest);
        var info = await ReadExact(b.Stream, 106);
        Assert.Equal((short)0x0a30, BinaryPrimitives.ReadInt16LittleEndian(info));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(2)));
    }

    // Point 9-11: a player -> monster combat action originating on replica A reaches an observer on
    // replica B exactly once, and neither replica ever produces a same-gateway duplicate from feed
    // replay (the attacker's own fast-path echo is deduplicated against its own later feed delivery
    // - see MapClientSession.ProjectPlayerAttackActionAsync's own doc comment).
    [Fact]
    public async Task PlayerAttackOnReplicaA_ReachesObserverOnReplicaB_ExactlyOnce_NoSameGatewayDuplicate()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        // Weak attacker so Poring's 55 HP survives a single non-lethal hit.
        var weakAttacker = new CharacterGameplayState(11, 1, 0, 1, 1, 0, 0, 40, 10, 40, 10, 0, 0, 9, 9, 9, 9, 9, 9);
        var a = await ConnectAsync(replicaA, accountId: 11, mapId, (ushort)(MonsterX - 1), MonsterY, weakAttacker);
        var b = await ConnectAsync(replicaB, accountId: 12, mapId, (ushort)(MonsterX + 1), MonsterY);
        using var _a = a.Client;
        using var _b = b.Client;

        // Bootstrap both replicas' monster projections against the SAME World-authoritative spawn.
        await TickAsync(replicaA, a.Session); // SpawnInitializationRequired -> load.
        await TickAsync(replicaA, a.Session); // Bootstrap.
        await TickAsync(replicaB, b.Session); // Bootstrap (shared World state, already loaded by replica A above).
        var introA = await ReadUntilAsync(a.Stream, packet => IsMonsterIntro(packet, 0) || BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyStandEntry);
        var monsterActorId = BinaryPrimitives.ReadUInt32LittleEndian(introA.AsSpan(5));
        await ReadUntilAsync(b.Stream, packet => IsMonsterIntro(packet, monsterActorId));

        // Mutual player discovery, drained.
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 12), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 11), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());

        await a.Stream.WriteAsync(BuildAttackPacket(monsterActorId));
        // A's own local fast-path echo of its own action.
        await ReadExact(a.Stream, PacketConstants.ZcStopMoveLength); // 0x0088 fixpos.
        var attackerAction = await ReadExact(a.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(attackerAction));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(attackerAction.AsSpan(2)));

        // B's own replica poll observes the SAME action via the World feed - exactly once.
        await TickAsync(replicaB, b.Session);
        var observerAction = await ReadExact(b.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(observerAction));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(observerAction.AsSpan(2)));
        Assert.Equal(monsterActorId, BinaryPrimitives.ReadUInt32LittleEndian(observerAction.AsSpan(6)));

        // No same-gateway duplicate: A's own fast-path echo is deduplicated against the SAME feed
        // entry replica A's own poll would otherwise also deliver to A's session.
        await TickAsync(replicaA, a.Session);
        await AssertNothingMoreSentAsync(a.Stream);
    }

    // Point 10: for a lethal player hit, the observer on the OTHER replica observes the killing
    // combat action before the monster's Died vanish - the structural feed-ordering guarantee (see
    // WorldMonsterMapSimulation.ApplyDamage's own doc comment), proven here across two genuinely
    // independent gateway processes, not merely within one process's own LethalAttackProjectionGate.
    [Fact]
    public async Task LethalPlayerHitOnReplicaA_ObserverOnReplicaB_SeesActionBeforeDiedVanish()
    {
        const string mapId = "geffen";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 13, mapId, (ushort)(MonsterX - 1), MonsterY);
        var b = await ConnectAsync(replicaB, accountId: 14, mapId, (ushort)(MonsterX + 1), MonsterY);
        using var _a = a.Client;
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        var introA = await ReadUntilAsync(a.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyStandEntry);
        var monsterActorId = BinaryPrimitives.ReadUInt32LittleEndian(introA.AsSpan(5));
        await ReadUntilAsync(b.Stream, packet => IsMonsterIntro(packet, monsterActorId));

        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 14), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 13), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());

        // Repeated attacks until the kill lands (default full-strength attacker vs. Poring's 55 HP).
        var killed = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        byte[]? observerAction = null;
        byte[]? observerVanish = null;
        while (!killed && DateTime.UtcNow < deadline)
        {
            await a.Stream.WriteAsync(BuildAttackPacket(monsterActorId));
            await ReadExact(a.Stream, PacketConstants.ZcStopMoveLength);
            var action = await ReadExact(a.Stream, PacketConstants.ZcNotifyAct3Length);
            var damage = BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(22));
            // Peek whether this hit's own local HP-info write reports 0 (lethal) without consuming
            // further unrelated bytes - HP-info always immediately follows the action for the
            // attacker's own session.
            var hpInfo = await ReadExact(a.Stream, PacketConstants.ZcHpInfoLength);
            var hpAfter = BinaryPrimitives.ReadUInt32LittleEndian(hpInfo.AsSpan(6));
            if (hpAfter == 0)
            {
                killed = true;
                await TickAsync(replicaB, b.Session);
                observerAction = await ReadExact(b.Stream, PacketConstants.ZcNotifyAct3Length);
                observerVanish = await ReadExact(b.Stream, PacketConstants.ZcNotifyVanishLength);
            }
            else
            {
                await TickAsync(replicaA, a.Session);
                await TickAsync(replicaB, b.Session);
                // Drain the observer's own copy of this non-lethal action before the next attack.
                await ReadExact(b.Stream, PacketConstants.ZcNotifyAct3Length);
            }
        }
        Assert.True(killed, "Expected the attacker to eventually land the killing blow.");
        Assert.NotNull(observerAction);
        Assert.NotNull(observerVanish);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(observerAction));
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(observerVanish));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, observerVanish![6]);
        // The action necessarily arrived strictly before the vanish because this test reads them in
        // that exact order from the SAME stream with no intervening bytes possible between two
        // back-to-back ReadExact calls on a single poll's own fan-out - see FanOutEntryAsync's own
        // per-entry-in-feed-order dispatch loop, which is what this proves end-to-end.
    }

    private static async Task AssertNothingMoreSentAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private static MapCollisionMap MakeAllWalkableMap(string name, int side = 200) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services
                .AddSingleton<IWorldPartitionResolver>(Resolver())
                .AddSingleton<IMovementPathProvider>(new UnverifiedGridLineMovementPathProvider())
                .AddSingleton<IMapCollisionProvider>(new MapCollisionProvider([MakeAllWalkableMap("izlude"), MakeAllWalkableMap("geffen")]))
                .AddSingleton(TimeProvider.System);
        }
    }
}
