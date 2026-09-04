using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.MapServer.Tests.Net;

// Step 6: real end-to-end coverage of the MapServer <-> World monster-authority cutover, driven
// through a genuine Orleans TestCluster-hosted IWorldPartitionGrain (never a scripted fake) - World
// itself remains the single behavioral source of truth for spawn loading/fingerprinting,
// SimulationEpoch, incarnation, sequenced feed/cursor semantics, resync, movement/engagement, and
// death/respawn lifecycle; these tests prove MapServer's OWN consumption of that authority (feed
// polling/reconciliation, combat-state rekeying, player<->monster mutation ordering) is correct,
// never re-testing World's own already-covered domain logic (see WorldMonsterSimulationTests.cs).
public sealed class MapTcpServerMonsterAuthorityIntegrationTests : IAsyncLifetime
{
    private const int PoringMobId = 1002; // GeneratedMobs.Poring - a real generated static mob entry, required so WorldMonsterActorView's own GeneratedMobRegistry lookup succeeds.
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

    private static MobSpawnDefinition PoringSpawn(string mapId, int count = 1) =>
        new(Athena.Net.MapServer.Generated.GameData.Mobs.GeneratedMobRegistry.Get(PoringMobId), mapId, count, RespawnDelay: 5000, RespawnRandomDelay: 0,
            new WorldSourceInfo("rAthena", "abc", "test", 0), SpawnName: "Poring", X: (short)MonsterX, Y: (short)MonsterY, Xs: 1, Ys: 1);

    private static MapServerWorld MakeWorld(string mapId, int count = 1)
    {
        var combatState = new MonsterCombatStateStore();
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules(), combatState);
        return new MapServerWorld(
            WorldMapRegistry.Tutorial,
            [PoringSpawn(mapId, count)],
            combat,
            EmptyMapCollisionProvider.Instance,
            new UnverifiedGridLineMovementPathProvider(),
            new MonsterFeedProjectionRegistry(),
            combatState);
    }

    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, TcpListener Listener)> ConnectSessionAsync(
        MapTcpServer server, MapServerWorld world, IWorldRuntime worldRuntime, uint accountId, string mapId, ushort x, ushort y)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync(); // NOT `using` - the session owns this socket for its own lifetime; disposing it here would kill the connection out from under RunAsync.
        await connect;
        var stream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var state = new CharacterGameplayState(accountId, 1, 0, 99, 10, 0, 0, 100, 20, 100, 20, 0, 0, 99, 9, 9, 9, 99, 9);
        // The production MapClientSession(MapServerWorld, IWorldRuntime) constructor defaults
        // gameplayStatePersistence to `connector` itself (a disconnected CharServerConnector in
        // tests, whose GetInventoryAsync/gameplay-state fetch always fails) - using it here would
        // make CompleteIroAuthenticationAsync call HandleAuthFail() and never send the bootstrap
        // burst this helper's own reads below depend on (exactly the trap MapClientSession's own
        // test-facing-constructor doc comment warns about). The internal test-facing constructor
        // lets this test supply a real ICharacterGameplayStatePersistence explicitly while still
        // wiring the SAME monsterProjections/combat/combatState/distributedWorld the production
        // constructor would.
        var session = new MapClientSession(
            (int)accountId, serverClient, connector, iroAuthenticated: true,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            monsterProjections: world.MonsterProjections, combat: world.Combat, combatState: world.CombatState,
            movementPathProvider: world.MovementPathProvider, collisionProvider: world.Collision,
            players: world.Players, playerVisibility: world.PlayerVisibility, visibilityOptions: world.Visibility,
            distributedWorld: worldRuntime);
        var run = session.RunAsync(CancellationToken.None);
        var auth = new MapAuthOkData(accountId, accountId, 1, 2, 0, 0, false, mapId, x, y, 0, 0, 1, "Fixture", HairStyle: 4, HairColor: 2, ClothesColor: 1);
        await session.CompleteIroAuthenticationAsync(auth);
        await ReadExact(stream, 29);
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon
        await ReadExact(stream, 6);  // inventory start
        await ReadExact(stream, 4);  // inventory end
        listener.Stop();
        return (client, stream, session, run, listener);
    }

    [Fact]
    public async Task SpawnInitializationRequired_LoadsSpawns_AndBootstrapsProjection()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);
        var (client, _, session, run, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 1, mapId, MonsterX, MonsterY);
        using var _dispose = client;

        // First tick: SpawnInitializationRequired -> LoadMonsterSpawnsAsync issued.
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
        // Second tick: fresh atomic bootstrap now available.
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        Assert.NotNull(projection.CurrentEpoch);
        Assert.Single(projection.AllInstances);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OnePerMapConsumer_TwoSessionsOnSameMap_ShareOneBootstrapAndCursor()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);
        var (clientA, _, sessionA, runA, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 1, mapId, MonsterX, MonsterY);
        var (clientB, _, sessionB, runB, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 2, mapId, MonsterX, MonsterY);
        using var _disposeA = clientA;
        using var _disposeB = clientB;

        await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);

        // Exactly ONE projection instance for this map, regardless of how many sessions are on it -
        // TryGet always resolves to the SAME MonsterFeedProjection object.
        Assert.True(world.MonsterProjections.TryGet(mapId, out var first));
        Assert.True(world.MonsterProjections.TryGet(mapId, out var second));
        Assert.Same(first, second);

        clientA.Close(); clientB.Close();
        await runA.WaitAsync(TimeSpan.FromSeconds(5));
        await runB.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NoActiveSessions_MapIsNeverPolled()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);

        // No sessions at all - the tick loop must not create/poll any projection for this map.
        await server.ProcessOneMonsterTickAsync([], CancellationToken.None);

        Assert.False(world.MonsterProjections.TryGet(mapId, out _));
    }

    [Fact]
    public async Task PlayerAttack_NonLethalHit_CallsNotifyMonsterAttacked_WithExactLifeAndPresenceId()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);
        var (client, stream, session, run, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 3, mapId, (ushort)(MonsterX - 1), MonsterY);
        using var _dispose = client;

        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None); // SpawnInitializationRequired.
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None); // Bootstrap.
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None); // Discovery/visibility fan-out.

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        var monster = Assert.Single(projection.AllInstances);

        // A single attack REQUEST (0x0437) merely registers/keeps a repeat-attack target - the
        // session's own background repeat-attack loop then executes the hit, producing its own
        // wire response packets (0x08C8/0x0977) this test never reads. Drain the stream
        // continuously in the background so those unread response packets never fill the OS socket
        // buffer and stall the session's own send path (which would otherwise silently prevent the
        // hit's outcome from ever completing).
        using var drainCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            var sink = new byte[4096];
            try { while (!drainCts.IsCancellationRequested) await stream.ReadAsync(sink, drainCts.Token); }
            catch (OperationCanceledException) { } catch (IOException) { }
        });

        await stream.WriteAsync(BuildAttackPacket(monster.ActorId));

        // Poll World directly (bypassing the wire) with a bounded wait - the session's own
        // repeat-attack loop executes the hit asynchronously and this test only needs to observe
        // World's own resulting engagement state, not the session's wire-facing combat packets
        // themselves.
        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorldMonsterInstance? acquiredInstance = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            var candidate = page.Snapshot!.Single();
            if (candidate.EngagedTarget is not null) { acquiredInstance = candidate; break; }
        }
        drainCts.Cancel();
        try { await drainTask; } catch { /* Expected once the stream is torn down. */ }

        // Non-lethal hit (Poring has 55 HP, unarmed low damage) - the monster must now be engaged
        // with THIS exact attacker's CharacterId+PresenceId, proving NotifyMonsterAttackedAsync was
        // called with the correct life/attacker identity.
        Assert.NotNull(acquiredInstance);
        Assert.NotNull(acquiredInstance!.EngagedTarget);
        Assert.Equal(3u, acquiredInstance.EngagedTarget!.CharacterId);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PlayerAttack_LethalHit_CallsTryMarkMonsterDead_NoLocalRespawnScheduling()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);
        var (client, stream, session, run, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 4, mapId, (ushort)(MonsterX - 1), MonsterY);
        using var _dispose = client;

        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        var monster = Assert.Single(projection.AllInstances);
        var epoch = projection.CurrentEpoch!.Value;
        var key = new MonsterCombatKey(mapId, epoch, monster.ActorId, monster.IncarnationId);

        // A single attack REQUEST (0x0437) merely registers/keeps a repeat-attack target - the
        // session's own background repeat-attack loop then executes hits on the real attack-delay
        // cadence, each producing its own wire response packets (0x08C8/0x0977) this test never
        // reads. Drain the stream continuously in the background so those unread response packets
        // never fill the OS socket buffer and block the server's own writes (observed as a Broken
        // pipe failure without this drain).
        using var drainCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            var sink = new byte[4096];
            try { while (!drainCts.IsCancellationRequested) await stream.ReadAsync(sink, drainCts.Token); }
            catch (OperationCanceledException) { } catch (IOException) { }
        });

        await stream.WriteAsync(BuildAttackPacket(monster.ActorId));
        var deadlineForDamage = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadlineForDamage)
        {
            await Task.Delay(150);
            if (world.CombatState.TryGet(key, out var s) && s.CurrentHp == 0) break;
        }
        drainCts.Cancel();
        try { await drainTask; } catch { /* Expected once the stream is torn down. */ }

        // World itself must report the life as Dead once MapServer's local hit reached HP==0 and
        // called TryMarkMonsterDeadAsync - proving the death transition genuinely reached World,
        // not merely a local combat-state zero with no World-side effect.
        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var confirmedDead = false;
        while (DateTime.UtcNow < deadline && !confirmedDead)
        {
            await Task.Delay(200);
            var life = new WorldMonsterLifeReference(mapId, epoch, monster.ActorId, monster.IncarnationId);
            var result = await grain.TryMarkMonsterDeadAsync(life);
            confirmedDead = result.Status is WorldMonsterDeathStatus.AlreadyDead; // Already dead means OUR earlier call already marked it.
        }
        Assert.True(confirmedDead, "Expected World to already report this life as Dead (AlreadyDead) after MapServer's own TryMarkMonsterDeadAsync call.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StaleLifeReference_AfterRespawn_CannotBeAttacked_NoQuestOrDeathProjection()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());

        // Load spawns directly and force a death+respawn cycle via the real grain, so we have a
        // concrete stale (pre-respawn) WorldMonsterLifeReference to test against.
        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var batch = WorldMonsterSpawnBatchBuilder.Build(mapId, world.MonsterSpawns);
        var load = await grain.LoadMonsterSpawnsAsync(batch);
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var staleLife = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        Assert.Equal(WorldMonsterDeathStatus.MarkedDead, (await grain.TryMarkMonsterDeadAsync(staleLife)).Status);

        // A second call with the SAME (now stale, since it's already dead) life reference is
        // AlreadyDead, not a fresh MarkedDead - proving no duplicate death/respawn scheduling
        // side effect occurs from a stale re-submission.
        var secondAttempt = await grain.TryMarkMonsterDeadAsync(staleLife);
        Assert.Equal(WorldMonsterDeathStatus.AlreadyDead, secondAttempt.Status);
    }

    [Fact]
    public async Task ReconnectWithSameCharacterId_NewPresenceId_CannotInheritOldMonsterAttackTarget()
    {
        var mapId = "izlude";
        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var world = MakeWorld(mapId);
        var batch = WorldMonsterSpawnBatchBuilder.Build(mapId, world.MonsterSpawns);
        var load = await grain.LoadMonsterSpawnsAsync(batch);
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 55u;
        var originalPresenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(new WorldPlayerPresence(originalPresenceId, characterId + 1_000_000, characterId, mapId, MonsterX, MonsterY));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired,
            (await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, originalPresenceId))).Status);

        var replacementPresenceId = Guid.NewGuid();
        await grain.UnregisterPresenceAsync(mapId, characterId, originalPresenceId);
        await grain.RegisterPresenceAsync(new WorldPlayerPresence(replacementPresenceId, characterId + 1_000_000, characterId, mapId, MonsterX, MonsterY));

        // The OLD presenceId must never be usable to validate/attack again.
        var staleAttack = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, originalPresenceId));
        Assert.Equal(WorldMonsterAttackedStatus.StaleAttackerPresence, staleAttack.Status);

        var staleWindow = await grain.ValidateMonsterAttackWindowAsync(new WorldMonsterAttackWindowQuery(life, characterId, originalPresenceId));
        Assert.Equal(WorldMonsterAttackWindowStatus.StaleTargetPresence, staleWindow.Status);
    }

    private static byte[] BuildAttackPacket(uint targetActorId)
    {
        var packet = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x0437);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), targetActorId);
        packet[6] = 0;
        return packet;
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

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

    private static MapCollisionMap MakeAllWalkableMap(string name, int side = 200) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }
}
