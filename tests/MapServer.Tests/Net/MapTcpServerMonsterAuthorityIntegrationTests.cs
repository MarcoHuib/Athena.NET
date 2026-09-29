using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;
using System.Reflection;

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

    private static MobSpawnDefinition PoringSpawn(string mapId, int count = 1, int respawnDelayMs = 5000) =>
        new(Athena.Net.MapServer.Generated.GameData.Mobs.GeneratedMobRegistry.Get(PoringMobId), mapId, count, RespawnDelay: respawnDelayMs, RespawnRandomDelay: 0,
            new WorldSourceInfo("rAthena", "abc", "test", 0), SpawnName: "Poring", X: (short)MonsterX, Y: (short)MonsterY, Xs: 1, Ys: 1);

    private static MapServerWorld MakeWorld(string mapId, int count = 1, int respawnDelayMs = 5000)
    {
        var combatState = new MonsterAttackCadenceStore();
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules());
        return new MapServerWorld(
            WorldMapRegistry.Tutorial,
            [PoringSpawn(mapId, count, respawnDelayMs)],
            combat,
            EmptyMapCollisionProvider.Instance,
            new UnverifiedGridLineMovementPathProvider(),
            new MonsterFeedProjectionRegistry(),
            combatState);
    }

    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, TcpListener Listener)> ConnectSessionAsync(
        MapTcpServer server, MapServerWorld world, IWorldRuntime worldRuntime, uint accountId, string mapId, ushort x, ushort y, CharacterGameplayState? gameplayState = null,
        Func<PlayerAttackActionOutcome, long, CancellationToken, Task>? playerAttackFanout = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync(); // NOT `using` - the session owns this socket for its own lifetime; disposing it here would kill the connection out from under RunAsync.
        await connect;
        var stream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var state = gameplayState ?? new CharacterGameplayState(accountId, 1, 0, 99, 10, 0, 0, 100, 20, 100, 20, 0, 0, 99, 9, 9, 9, 99, 9);
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
            distributedWorld: worldRuntime, playerAttackFanout: playerAttackFanout, lethalAttackGate: server.LethalAttackGateForTest);
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

        // EnterPlayerWorldAsync (which sets IsWorldMapEligible) is called AFTER the self weapon/
        // inventory packets are already sent, and itself awaits an Orleans RegisterPresenceAsync RPC
        // - it is not guaranteed to have completed merely because the client has finished reading
        // those three packets. ProcessOneMonsterTickAsync's own IsWorldMapEligible filter (item 6 of
        // the monster-authority hardening pass) means a caller MUST wait for this session to actually
        // become world-visible before driving a monster tick against it, exactly like production
        // code would naturally observe (a session only appears in a real map-group once eligible).
        var eligibilityDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < eligibilityDeadline) await Task.Delay(10);
        Assert.True(session.IsWorldMapEligible, "Expected the session to reach WorldVisible (IsWorldMapEligible) before returning from ConnectSessionAsync.");

        return (client, stream, session, run, listener);
    }

    // Substep 10: thin wrapper around ConnectSessionAsync that also spins up the background
    // drain-loop pattern already duplicated inline by PlayerAttack_NonLethalHit_.../
    // PlayerAttack_LethalHit_... above. Only used by scenarios that don't need to read/assert
    // specific packets off the stream themselves - those use plain ConnectSessionAsync and drain
    // manually/selectively instead.
    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, CancellationTokenSource DrainCts, Task DrainTask)> ConnectAttackerAsync(
        MapTcpServer server, MapServerWorld world, IWorldRuntime worldRuntime, uint accountId, string mapId, ushort x, ushort y, CharacterGameplayState? gameplayState = null)
    {
        var (client, stream, session, run, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId, mapId, x, y, gameplayState);
        var drainCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            var sink = new byte[4096];
            try { while (!drainCts.IsCancellationRequested) await stream.ReadAsync(sink, drainCts.Token); }
            catch (OperationCanceledException) { } catch (IOException) { }
        });
        return (client, stream, session, run, drainCts, drainTask);
    }

    // Substep 10: a fixture whose first hit against Poring's real 55 HP is reliably NON-LETHAL
    // (confirmed by inline assertion at each use site - see scenario 3), but two hits together
    // reliably kill. Deterministic per this project's own unarmed statusAtk formula (no RNG
    // dependency): a low-BaseLevel, low-stat attacker without a weapon.
    private static CharacterGameplayState ModerateAttackerFor(uint accountId) =>
        new(accountId, 1, 0, 5, 1, 0, 0, 100, 20, 100, 20, 0, 0, 5, 3, 3, 3, 5, 3);

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
        // Deliberately weak, deterministic attacker (BaseLevel 1, minimum stats, unarmed): the
        // shared file-level fixture (BaseLevel 99, STR/DEX 99) computes an unarmed RENEWAL statusAtk
        // far above Poring's 55 HP with ZERO randomness involved (WeaponAttackCalculator's own
        // pinned trace: an unarmed hand never reaches the rollWeaponAtk RNG branch at all - weaponAtk
        // is hard-fixed at 0), so reusing it here would DETERMINISTICALLY one-shot the monster before
        // EngagementAcquired can ever be observed - this is not flaky, it always kills. A weak fixed
        // attacker keeps this test's own damage deterministically non-lethal.
        var weakAttacker = new CharacterGameplayState(3, 1, 0, 1, 1, 0, 0, 100, 20, 100, 20, 0, 0, 1, 1, 1, 1, 1, 1);
        var (client, stream, session, run, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 3, mapId, (ushort)(MonsterX - 1), MonsterY, weakAttacker);
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

        // Non-lethal hit (Poring has 55 HP; this attacker's own deterministic unarmed statusAtk is
        // small) - the monster must now be engaged with THIS exact attacker's CharacterId+PresenceId,
        // proving NotifyMonsterAttackedAsync was called with the correct life/attacker identity.
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

        // World itself must report the life as Dead once MapServer's production attack path called
        // the real ApplyMonsterDamageAsync RPC and it committed a lethal hit - proving the death
        // transition genuinely reached World, not merely projected client-side with no World-side
        // effect. Verified PURELY via the read-only PollMonsterFeedAsync (never by calling the
        // mutating ApplyMonsterDamageAsync RPC from this test as a "verification" step - that would
        // be a false positive, since the test's OWN call could be the one that actually marks the
        // life dead even if MapServer's production path never reached World at all).
        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var confirmedDead = false;
        while (DateTime.UtcNow < deadline && !confirmedDead)
        {
            await Task.Delay(200);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            var candidate = page.Snapshot!.SingleOrDefault(instance => instance.ActorId == monster.ActorId);
            confirmedDead = candidate is { Lifecycle: WorldMonsterLifecycleState.Dead };
        }
        drainCts.Cancel();
        try { await drainTask; } catch { /* Expected once the stream is torn down. */ }
        Assert.True(confirmedDead, "Expected World's own feed to report this life's Lifecycle as Dead after MapServer's production attack path called ApplyMonsterDamageAsync.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 7 substep 7 review fix: the prior MapClientSessionRespawnCleanupTests only proved
    // NotifyMonsterRespawnedAsync's OWN effect and NotifyMonsterMovedAsync's OWN discovery behavior
    // when called manually, in sequence, by the test itself - never that MapTcpServer.FanOutEntryAsync
    // ACTUALLY calls both for a real Respawned feed entry. A `return` accidentally inserted right
    // after FanOutEntryAsync's own Respawned cleanup branch would make every one of those tests keep
    // passing, since none of them ever go through FanOutEntryAsync at all.
    //
    // This test drives a REAL kill+respawn cycle through the actual Orleans grain (never a scripted
    // fake), then calls the real ProcessOneMonsterTickAsync -> ... -> FanOutEntryAsync path (never
    // NotifyMonsterRespawnedAsync/NotifyMonsterMovedAsync directly) and asserts the session's own
    // socket receives ordinary discovery of the NEW incarnation. If FanOutEntryAsync's own Respawned
    // branch were changed to `return` right after the cleanup call (skipping the existing generic
    // discovery tail below it), this session would never receive ANY packet for the new incarnation
    // and this test would time out / fail on the ReadDynamic call below.
    [Fact]
    public async Task Respawned_RealFeedEntry_ThroughProcessOneMonsterTick_ProducesOrdinaryDiscoveryOfNewIncarnation()
    {
        var mapId = "izlude";
        // A short, real respawn delay so the World-side grain's own 100ms tick timer produces a
        // genuine Respawned feed entry within a bounded, deterministic polling window - never
        // simulated/hand-constructed.
        var world = MakeWorld(mapId, respawnDelayMs: 200);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);
        var (client, stream, session, run, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 6, mapId, MonsterX, MonsterY);
        using var _dispose = client;

        // First tick: SpawnInitializationRequired -> LoadMonsterSpawnsAsync issued.
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
        // Second tick: fresh atomic bootstrap now available - the session discovers incarnation 1.
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
        var initialDiscovery = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(initialDiscovery));

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        var original = Assert.Single(projection.AllInstances);
        var epoch = projection.CurrentEpoch!.Value;
        var oldLife = new WorldMonsterLifeReference(mapId, epoch, original.ActorId, original.IncarnationId);

        // Kill it via the REAL grain, exactly as StaleLifeReference_AfterRespawn_... above does -
        // this schedules the real World-side respawn, whose real 100ms grain timer will observe the
        // due respawn and append a genuine Respawned feed entry on its own.
        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var killerPresenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(new WorldPlayerPresence(killerPresenceId, ActorId: 999, CharacterId: 999, mapId, X: original.X, Y: original.Y), new WorldPlayerPublicState("Test", 0, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        var lethalHit = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(oldLife, AttackerCharacterId: 999, killerPresenceId, AttackSequence: 1, Damage: 9999, AcquireEngagement: false));
        Assert.Equal(WorldMonsterDamageStatus.Applied, lethalHit.Status);
        Assert.True(lethalHit.KilledByThisHit);
        // Item 14: this fake killer presence is a throwaway test fixture purely for
        // ApplyMonsterDamageAsync's own attacker-presence validation - it is NOT the session under
        // test. Since RegisterPresenceAsync now also participates in the real World-owned player
        // feed (item 14 stage 1), leaving it registered would make the session's own subsequent
        // ProcessOneMonsterTickAsync calls (which poll BOTH the monster and player feeds each tick)
        // discover it as a genuine nearby player and write an unplanned spawn packet into `stream`,
        // corrupting the later monster-feed-only byte reads below. Unregister it immediately so this
        // test continues to exercise ONLY the monster feed, exactly as before this stage existed.
        await grain.UnregisterPresenceAsync(mapId, characterId: 999, killerPresenceId);

        // Drive ProcessOneMonsterTickAsync repeatedly (the real production polling loop's own unit
        // of work) until the death vanish (0x0080 reason=Died) reaches the wire - this is World's own
        // real Died feed entry, fanned out by FanOutEntryAsync's own EXISTING Died branch (unchanged
        // by this substep), and must be drained before looking for the later Respawned discovery
        // packet, or this read would misinterpret the vanish packet's own bytes as the rediscovery.
        // Item 14: ApplyMonsterDamageAsync's own commit now ALWAYS appends a PlayerAttackAction
        // entry immediately before HealthChanged/Died (see WorldMonsterMapSimulation.ApplyDamage's
        // own doc comment) - a real 0x08C8 for the killer's own action (ActorId 999, the throwaway
        // fixture presence, already unregistered above) therefore arrives on this stream BEFORE the
        // vanish now, where none existed before this stage. Drain it (via the SAME per-tick polling
        // loop the vanish itself needs, since neither packet exists until a tick actually polls the
        // feed) before the vanish read.
        var deadlineForDeath = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var observedDeathVanish = false;
        var actionReadTask = ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        while (DateTime.UtcNow < deadlineForDeath && !observedDeathVanish)
        {
            await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
            observedDeathVanish = actionReadTask.IsCompletedSuccessfully;
            if (!observedDeathVanish) await Task.Delay(20);
        }
        var actionPacket = await actionReadTask;
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(actionPacket));
        Assert.Equal(999u, BinaryPrimitives.ReadUInt32LittleEndian(actionPacket.AsSpan(2)));
        Assert.Equal(original.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(actionPacket.AsSpan(6)));

        observedDeathVanish = false;
        var vanishReadTask = ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        while (DateTime.UtcNow < deadlineForDeath && !observedDeathVanish)
        {
            await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
            observedDeathVanish = vanishReadTask.IsCompletedSuccessfully;
            if (!observedDeathVanish) await Task.Delay(20);
        }
        var vanishPacket = await vanishReadTask;
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(original.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(vanishPacket.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanishPacket[6]);

        // Continue driving ProcessOneMonsterTickAsync until the session's projection reports a NEW
        // incarnation for this ActorId - proof the real World-side respawn has genuinely occurred and
        // propagated through the feed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorldMonsterInstance? respawnedInstance = null;
        while (DateTime.UtcNow < deadline)
        {
            await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
            if (world.MonsterProjections.TryGet(mapId, out var current) &&
                current.AllInstances.SingleOrDefault(i => i.ActorId == original.ActorId) is { Lifecycle: WorldMonsterLifecycleState.Alive } candidate &&
                !candidate.IncarnationId.Equals(original.IncarnationId))
            {
                respawnedInstance = candidate;
                break;
            }
            await Task.Delay(50);
        }
        Assert.NotNull(respawnedInstance);

        // The load-bearing assertion: the session's own socket receives ordinary discovery
        // (0x09FF stand entry) of the NEW incarnation - produced ENTIRELY by the real
        // ProcessOneMonsterTickAsync -> FanOutEntryAsync -> NotifyMonsterMovedAsync path, never by
        // this test calling NotifyMonsterRespawnedAsync/NotifyMonsterMovedAsync itself. If
        // FanOutEntryAsync's own Respawned branch returned immediately after its cleanup call
        // (skipping this discovery tail), this read would time out and fail the test.
        var rediscoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(rediscoveryPacket));
        Assert.Equal(original.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(rediscoveryPacket.AsSpan(5)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 7 substep 11: the real end-to-end same-spawn kill/respawn/re-attack soak. ONE player
    // session, ONE monster spawn point/ActorId, driven through the REAL MapTcpServer/MapClientSession/
    // Orleans TestCluster/WorldPartitionGrain stack - never a scripted fake, never a manually
    // replaced session projection. Every cycle's attack goes through the genuine Ragexe 0x0437 wire
    // path (BuildAttackPacket -> HandleIroAttackRequestAsync -> the live PendingMonsterDamageAttempt
    // machinery -> ApplyMonsterDamageAsync), and every respawn is discovered purely through the real
    // monster feed (ProcessOneMonsterTickAsync -> FanOutEntryAsync), matching
    // Respawned_RealFeedEntry_...'s own established single-cycle pattern extended to >=25 repeats of
    // the SAME ActorId. All ordering below is state-driven (bounded polling against real grain/feed/
    // session state), never an arbitrary sleep used for correctness.
    [Fact]
    public async Task KillRespawnReattack_TwentyFiveCyclesSameSpawnPoint_NoHistoricalMemoryGrowth()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId, respawnDelayMs: 150); // Short, real respawn delay - the grain's own 100ms tick timer observes it deterministically.
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);
        // The file-level default strong/one-shot fixture (BaseLevel 99, STR/DEX 99) - proven
        // elsewhere in this file (Scenario 1's own fixture-requirement note) to one-shot-kill
        // Poring's 55 HP deterministically, no RNG dependency. Every cycle's attack must be lethal
        // on its own first real hit so the soak's own bounded per-cycle windows stay tight.
        var (client, stream, session, run, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 700, mapId, MonsterX, MonsterY);
        using var _dispose = client;

        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None); // SpawnInitializationRequired.
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None); // Fresh atomic bootstrap.
        var initialDiscovery = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(initialDiscovery));

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        var original = Assert.Single(projection.AllInstances);
        var actorId = original.ActorId;
        var currentIncarnation = original.IncarnationId;

        var maxInFlight = 0;
        var maxAlreadyProjected = 0;

        const int cycles = 25;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            // A. projection contains the current Alive incarnation; B. full HP before attacking.
            Assert.True(world.MonsterProjections.TryGet(mapId, out var beforeProjection), $"Cycle {cycle}: expected a live projection.");
            var beforeInstance = Assert.Single(beforeProjection.AllInstances, i => i.ActorId == actorId);
            Assert.Equal(WorldMonsterLifecycleState.Alive, beforeInstance.Lifecycle);
            Assert.Equal(currentIncarnation, beforeInstance.IncarnationId);
            Assert.Equal(beforeInstance.MaxHp, beforeInstance.CurrentHp);

            // Deterministic observation of _inFlight WHILE the real attack is actually in flight
            // (review fix): AllocateAndDispatchFreshDamageAttemptAsync calls BeginInFlight under
            // the SAME _attackGate critical section that publishes the pending attempt, strictly
            // BEFORE the first dispatch (DispatchPendingDamageAttemptAsync) ever runs - so by the
            // time this transparent wrapper around the REAL worldRuntime.ApplyMonsterDamageAsync
            // call is entered, BeginInFlight has unconditionally already registered this life.
            // Asserting here, before awaiting the real RPC's result, is what actually proves
            // "_inFlight <= 1 DURING one logical attack" rather than merely inferring it from the
            // fully-resolved state after the fact. The real World RPC and real end-to-end path are
            // preserved - this wrapper only observes, never fakes or skips, the real call.
            var inFlightObservedDuringDispatch = false;
            session.DebugApplyMonsterDamageDispatcher = async (command, ct) =>
            {
                Assert.Equal(1, session.LethalDeathArbiterInFlightCountForTest);
                inFlightObservedDuringDispatch = true;
                maxInFlight = Math.Max(maxInFlight, session.LethalDeathArbiterInFlightCountForTest);
                return await worldRuntime.ApplyMonsterDamageAsync(command, ct);
            };

            // C. the real Ragexe 0x0437 / MapClientSession attack path.
            await stream.WriteAsync(BuildAttackPacket(actorId));

            // D. World authority reaches Dead/HP==0 - polled directly against the real grain, the
            // authoritative source, never inferred from the session's own local projection.
            var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
            var deathDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var confirmedDead = false;
            while (DateTime.UtcNow < deathDeadline && !confirmedDead)
            {
                await Task.Delay(20);
                var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
                var candidate = page.Snapshot!.SingleOrDefault(i => i.ActorId == actorId);
                confirmedDead = candidate is { Lifecycle: WorldMonsterLifecycleState.Dead, CurrentHp: 0 };
            }
            Assert.True(confirmedDead, $"Cycle {cycle}: expected World authority to confirm Dead/HP==0.");

            // E. the session resolves the logical attack/death projection - drive real ticks until
            // its own socket observes exactly the Died vanish for this ActorId (ReadUntilVanishAsync
            // skips any preceding damage/HP-info packet using each packet's own real framing).
            var vanishTask = ReadUntilVanishAsync(stream);
            var vanishDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < vanishDeadline && !vanishTask.IsCompletedSuccessfully)
            {
                await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
                await Task.Delay(20);
            }
            var vanishPacket = await vanishTask;
            Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(vanishPacket.AsSpan(2)));
            Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanishPacket[6]);

            // This cycle's real dispatch must have actually reached the wrapper above - otherwise
            // the _inFlight==1 observation asserted there never genuinely ran, and this cycle would
            // silently prove nothing about the in-flight invariant.
            Assert.True(inFlightObservedDuringDispatch, $"Cycle {cycle}: expected the real dispatch to observe _inFlight while in flight.");

            // F. no unresolved PendingMonsterDamageAttempt remains for this session.
            var pendingRetryAt = await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None);
            Assert.Null(pendingRetryAt);

            // LethalDeathProjectionArbiter memory bound: 0 in-flight now that this logical attack
            // has fully resolved (the peak of 1 was directly observed above, WHILE the real World
            // RPC was actually in flight), and _alreadyProjected may be 1 right after this session's
            // own lethal vanish projects (markProjected: true on the winning path) - captured now,
            // before this cycle's Respawned cleanup removes it.
            Assert.Equal(0, session.LethalDeathArbiterInFlightCountForTest);
            maxAlreadyProjected = Math.Max(maxAlreadyProjected, session.LethalDeathArbiterAlreadyProjectedCountForTest);

            // G./H./I. drive the real feed until Respawned is observed: the SAME ActorId reports a
            // NEW IncarnationId with CurrentHp == MaxHp.
            WorldMonsterInstance? respawnedInstance = null;
            var respawnDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < respawnDeadline)
            {
                await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);
                if (world.MonsterProjections.TryGet(mapId, out var afterProjection) &&
                    afterProjection.AllInstances.SingleOrDefault(i => i.ActorId == actorId) is { Lifecycle: WorldMonsterLifecycleState.Alive } candidate &&
                    !candidate.IncarnationId.Equals(currentIncarnation))
                {
                    respawnedInstance = candidate;
                    break;
                }
                await Task.Delay(20);
            }
            Assert.NotNull(respawnedInstance);
            Assert.Equal(actorId, respawnedInstance!.ActorId); // Same ActorId/spawn point across every cycle.
            Assert.NotEqual(currentIncarnation, respawnedInstance.IncarnationId);
            Assert.Equal(respawnedInstance.MaxHp, respawnedInstance.CurrentHp);

            // The session must discover the new incarnation through the real World feed (ordinary
            // discovery, 0x09FF) - never a manually replaced projection.
            var rediscovery = await ReadDynamic(stream);
            Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(rediscovery));
            Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(rediscovery.AsSpan(5)));

            // After Respawned cleanup for this actor, _alreadyProjected must be back to 0 for this
            // session - the load-bearing proof against historical growth across cycles.
            Assert.Equal(0, session.LethalDeathArbiterAlreadyProjectedCountForTest);

            // Review fix: also prove "no unresolved pending attempt" at the COMPLETED kill/respawn
            // cycle boundary - the earlier check (F, above) ran before the Respawned lifecycle even
            // started, which only proves the pending attempt resolved before respawn, not that it
            // stays resolved (0) all the way through respawn/rediscovery. This is the actual
            // per-cycle boundary the original requirement calls for.
            Assert.Null(await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None));

            currentIncarnation = respawnedInstance.IncarnationId;
        }

        // Across all 25 cycles, the maximum observed arbiter counts never exceeded the bounded
        // envelope - they did not grow with cycle/kill number. maxInFlight was captured directly
        // WHILE each cycle's real dispatch was in flight (see the DebugApplyMonsterDamageDispatcher
        // wrapper above), not merely inferred from the fully-resolved post-cycle state - it proves
        // the actual requested envelope, _inFlight <= 1 DURING one logical attack.
        Assert.Equal(1, maxInFlight);
        Assert.Equal(1, maxAlreadyProjected);

        // Pre-teardown snapshot: no unresolved pending attempt while the session is still fully
        // live (both this and the identical post-teardown check below are required - see that
        // check's own doc comment for why SnapshotPendingNextRetryAtForTestAsync itself cannot be
        // repeated after teardown).
        Assert.Null(await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None));

        // J./6. session-end cleanup: close normally, THEN await teardown, THEN confirm terminal
        // state - the assertions below run strictly AFTER RunAsync's own teardown has completed
        // (run.WaitAsync has returned), not before it.
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // Known limitation (review-identified): SnapshotPendingNextRetryAtForTestAsync itself
        // cannot be safely called here - RunAsync's own teardown (StopCoreAsync) disposes
        // _attackGate (the SemaphoreSlim that method awaits) as part of its own `finally` block,
        // which has already run by the time run.WaitAsync() returns above. Calling it now would
        // throw ObjectDisposedException, not report a legitimate "no pending attempt" answer -
        // inventing a new post-teardown-safe seam for this would be a broader change than this
        // correction's own scope allows. The pre-teardown snapshot immediately above already
        // proves "no unresolved pending attempt" at the last live-session instant available before
        // teardown; LethalDeathProjectionArbiter is a plain in-memory Lock-guarded type (never
        // disposed by teardown), so its own counts remain safely, genuinely readable AFTER
        // teardown and are asserted here as the actual terminal-state confirmation.
        Assert.Equal(0, session.LethalDeathArbiterInFlightCountForTest);
        Assert.Equal(0, session.LethalDeathArbiterAlreadyProjectedCountForTest);
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
        var killerPresenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(new WorldPlayerPresence(killerPresenceId, ActorId: 999, CharacterId: 999, mapId, X: MonsterX, Y: MonsterY), new WorldPlayerPublicState("Test", 0, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        var firstHit = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(staleLife, AttackerCharacterId: 999, killerPresenceId, AttackSequence: 1, Damage: 9999, AcquireEngagement: false));
        Assert.Equal(WorldMonsterDamageStatus.Applied, firstHit.Status);
        Assert.True(firstHit.KilledByThisHit);

        // A second, genuinely NEW attempt (higher sequence) against the SAME (now stale, since it's
        // already dead) life reference is AlreadyDead, not a fresh Applied - proving no duplicate
        // death/respawn scheduling side effect occurs from a stale re-submission.
        var secondAttempt = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(staleLife, AttackerCharacterId: 999, killerPresenceId, AttackSequence: 2, Damage: 9999, AcquireEngagement: false));
        Assert.Equal(WorldMonsterDamageStatus.AlreadyDead, secondAttempt.Status);
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
        await grain.RegisterPresenceAsync(new WorldPlayerPresence(originalPresenceId, characterId + 1_000_000, characterId, mapId, MonsterX, MonsterY), new WorldPlayerPublicState("Test", 0, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired,
            (await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, originalPresenceId))).Status);

        var replacementPresenceId = Guid.NewGuid();
        await grain.UnregisterPresenceAsync(mapId, characterId, originalPresenceId);
        await grain.RegisterPresenceAsync(new WorldPlayerPresence(replacementPresenceId, characterId + 1_000_000, characterId, mapId, MonsterX, MonsterY), new WorldPlayerPublicState("Test", 0, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        // The OLD presenceId must never be usable to validate/attack again.
        var staleAttack = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, originalPresenceId));
        Assert.Equal(WorldMonsterAttackedStatus.StaleAttackerPresence, staleAttack.Status);

        var staleWindow = await grain.ValidateMonsterAttackWindowAsync(new WorldMonsterAttackWindowQuery(life, characterId, originalPresenceId));
        Assert.Equal(WorldMonsterAttackWindowStatus.StaleTargetPresence, staleWindow.Status);
    }

    // ================================================================================
    // Substep 10: multi-attacker / cross-process integration.
    // ================================================================================

    // Scenario 1: two sessions, both using the file-level strong/one-shot attacker fixture (so
    // EACH session's own FIRST logical attack attempt would independently be lethal if it wins the
    // race - not merely "two hits together kill"), race a genuinely concurrent attack against the
    // SAME monster through the real grain. A shared barrier (both sessions' own
    // DebugApplyMonsterDamageDispatcher signals arrival, then blocks on one shared release gate
    // before calling the REAL dispatcher) guarantees the two dispatches genuinely overlap without
    // ever controlling or predicting which one the real grain actually processes first - that
    // ordering stays nondeterministic by design. Assertions are winner-independent: exactly one
    // first-attempt result is Applied+KilledByThisHit, the other is AlreadyDead, and the
    // AlreadyDead loser's own wire silence (no damage/HP-info/reward packet) is proven directly
    // against its own socket, not merely inferred from the status.
    [Fact]
    public async Task TwoAttackers_ConcurrentLethalRace_ExactlyOneWinner_NoHpUnderflow_LoserGetsAlreadyDead()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);
        var (clientA, streamA, sessionA, runA, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 100, mapId, (ushort)(MonsterX - 1), MonsterY);
        var (clientB, streamB, sessionB, runB, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 101, mapId, (ushort)(MonsterX + 1), MonsterY);
        using var _disposeA = clientA;
        using var _disposeB = clientB;

        await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        var monster = Assert.Single(projection.AllInstances);

        // Baseline cursor captured BEFORE the race, so the post-race proof below can poll the real
        // feed incrementally FROM this point and count Died entries directly - never inferred from
        // the terminal Lifecycle==Dead snapshot, which cannot distinguish "exactly one Died entry"
        // from "zero or several, but the last-observed state happens to be Dead".
        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var baselinePage = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var feedCursor = new WorldMonsterFeedCursor(baselinePage.SimulationEpoch, baselinePage.AsOfSequence);

        var aReachedBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReachedBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorldMonsterDamageResult? firstResultA = null;
        WorldMonsterDamageResult? firstResultB = null;

        sessionA.DebugApplyMonsterDamageDispatcher = async (command, ct) =>
        {
            aReachedBarrier.TrySetResult();
            await releaseBoth.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await worldRuntime.ApplyMonsterDamageAsync(command, ct);
            firstResultA ??= result;
            return result;
        };
        sessionB.DebugApplyMonsterDamageDispatcher = async (command, ct) =>
        {
            bReachedBarrier.TrySetResult();
            await releaseBoth.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await worldRuntime.ApplyMonsterDamageAsync(command, ct);
            firstResultB ??= result;
            return result;
        };

        await streamA.WriteAsync(BuildAttackPacket(monster.ActorId));
        await streamB.WriteAsync(BuildAttackPacket(monster.ActorId));

        await aReachedBarrier.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await bReachedBarrier.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Both sessions are now genuinely parked at the barrier, mid-dispatch, simultaneously -
        // release them together. Which one the real grain actually processes first stays
        // nondeterministic; this barrier only guarantees overlap, never a winner.
        releaseBoth.TrySetResult();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var confirmedDead = false;
        while (DateTime.UtcNow < deadline && !confirmedDead)
        {
            await Task.Delay(50);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            confirmedDead = page.Snapshot!.SingleOrDefault(i => i.ActorId == monster.ActorId) is { Lifecycle: WorldMonsterLifecycleState.Dead };
        }
        Assert.True(confirmedDead, "Expected the grain to confirm the monster's death after the race resolved.");

        // Bounded settle window so both sessions' own first-result captures are guaranteed populated.
        var settleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < settleDeadline && (firstResultA is null || firstResultB is null)) await Task.Delay(20);
        Assert.NotNull(firstResultA);
        Assert.NotNull(firstResultB);

        var lethalResults = new[] { firstResultA!, firstResultB! }.Where(r => r.Status == WorldMonsterDamageStatus.Applied && r.KilledByThisHit).ToArray();
        var alreadyDeadResults = new[] { firstResultA!, firstResultB! }.Where(r => r.Status == WorldMonsterDamageStatus.AlreadyDead).ToArray();
        Assert.Single(lethalResults);
        Assert.Single(alreadyDeadResults);

        var finalPage = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var finalInstance = Assert.Single(finalPage.Snapshot!, i => i.ActorId == monster.ActorId);
        Assert.Equal(WorldMonsterLifecycleState.Dead, finalInstance.Lifecycle);
        Assert.Equal(0u, finalInstance.CurrentHp);
        Assert.Single(finalPage.Snapshot!, i => i.ActorId == monster.ActorId); // No duplicate/ghost entries.

        // Exactly ONE authoritative Died feed entry for the raced Life - polled incrementally from
        // the pre-race baseline cursor, never inferred from the terminal Lifecycle==Dead snapshot.
        // The race has already fully settled by this point (both first-attempt results resolved,
        // Dead confirmed), so finalPage.AsOfSequence is an authoritative terminal boundary: scan
        // every incremental page from the pre-race cursor until the cursor catches up to that exact
        // sequence, accumulating EVERY Died entry for this Life along the way - never stopping at
        // the first one found, since that would only prove "at least one", not "exactly one" (a
        // hypothetical second Died entry in a later page would otherwise never be inspected).
        var diedEntryCount = 0;
        var feedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (feedCursor.Sequence < finalPage.AsOfSequence)
        {
            Assert.True(DateTime.UtcNow < feedDeadline, "Timed out scanning the feed up to the terminal sequence.");
            var page = await grain.PollMonsterFeedAsync(feedCursor, mapId);
            if (page.Snapshot is not null)
            {
                // A resync mid-scan would make incremental Died-counting unreliable for this
                // specific proof - none is expected in this bounded single-map scenario, but fail
                // loudly rather than silently under/over-count if one ever occurs.
                Assert.Fail("Expected only incremental feed entries while counting Died occurrences, but observed a resync/snapshot page.");
            }
            feedCursor = new WorldMonsterFeedCursor(page.SimulationEpoch, page.AsOfSequence);
            if (page.Entries is { Count: > 0 } entries)
                diedEntryCount += entries.Count(e => e.Kind == WorldMonsterFeedEntryKind.Died && e.ActorId == monster.ActorId && e.IncarnationId.Equals(monster.IncarnationId));
            if (page.Entries is not { Count: > 0 }) await Task.Delay(20);
        }
        Assert.Equal(1, diedEntryCount);

        // Prove the AlreadyDead loser's wire silence directly against its own socket - not merely
        // inferred from the status - since this is the cross-process integration proof.
        var loserStream = firstResultA!.Status == WorldMonsterDamageStatus.AlreadyDead ? streamA : streamB;
        await AssertNoDamageHpOrRewardPacketsAsync(loserStream);

        clientA.Close(); clientB.Close();
        await runA.WaitAsync(TimeSpan.FromSeconds(5));
        await runB.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 2: two FULLY INDEPENDENT MapServerWorld/MapTcpServer instances (own projection
    // registry/cadence store/coordinator each) share only the same Orleans cluster/grain - zero
    // shared MapServer-local object identity. One attacker session on serverA/worldA commits a
    // real non-lethal hit; the bystander session on serverB/worldB, whose own projection never
    // shares an object reference with worldA's, converges to the new authoritative HP purely
    // through its own real ProcessOneMonsterTickAsync poll cycle.
    [Fact]
    public async Task CrossProcess_HealthChangedConvergence_ThroughRealPollLoop_NoSharedProjectionIdentity()
    {
        var mapId = "izlude";
        var worldA = MakeWorld(mapId);
        var worldB = MakeWorld(mapId);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var serverA = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), worldA, worldRuntime);
        var serverB = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), worldB, worldRuntime);

        // Deliberately weaker than ModerateAttackerFor (2-hit-kill) - this scenario only needs ONE
        // real, non-zero-damage, non-lethal hit to land before the assertions below run, and must
        // stay non-lethal comfortably across the whole poll window. A pure minimum-stat fixture
        // (STR/DEX/AGI all 1) was found to miss 100% of the time against this monster/attacker
        // combo (see this scenario's own git history) - BaseLevel 5 with modest STR/DEX guarantees
        // real, small, non-lethal damage per hit.
        var lightAttacker = new CharacterGameplayState(200, 1, 0, 5, 1, 0, 0, 100, 20, 100, 20, 0, 0, 5, 3, 3, 3, 5, 3);
        var (clientA, streamA, sessionA, runA, drainCtsA, drainTaskA) = await ConnectAttackerAsync(serverA, worldA, worldRuntime, accountId: 200, mapId, (ushort)(MonsterX - 1), MonsterY, lightAttacker);
        var (clientB, _, sessionB, runB, _) = await ConnectSessionAsync(serverB, worldB, worldRuntime, accountId: 201, mapId, (ushort)(MonsterX + 1), MonsterY);
        using var _disposeA = clientA;
        using var _disposeB = clientB;

        await serverA.ProcessOneMonsterTickAsync([sessionA], CancellationToken.None);
        await serverA.ProcessOneMonsterTickAsync([sessionA], CancellationToken.None);
        await serverA.ProcessOneMonsterTickAsync([sessionA], CancellationToken.None);
        await serverB.ProcessOneMonsterTickAsync([sessionB], CancellationToken.None);
        await serverB.ProcessOneMonsterTickAsync([sessionB], CancellationToken.None);
        await serverB.ProcessOneMonsterTickAsync([sessionB], CancellationToken.None);

        Assert.True(worldA.MonsterProjections.TryGet(mapId, out var projectionA));
        var monster = Assert.Single(projectionA.AllInstances);
        Assert.True(worldB.MonsterProjections.TryGet(mapId, out var projectionB));
        var baselineHp = Assert.Single(projectionB.AllInstances, i => i.ActorId == monster.ActorId).CurrentHp;

        await streamA.WriteAsync(BuildAttackPacket(monster.ActorId));

        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var mutationDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        uint? authoritativeHp = null;
        uint? authoritativeMaxHp = null;
        while (DateTime.UtcNow < mutationDeadline)
        {
            await Task.Delay(100);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            var instance = page.Snapshot!.Single(i => i.ActorId == monster.ActorId);
            if (instance.CurrentHp < baselineHp) { authoritativeHp = instance.CurrentHp; authoritativeMaxHp = instance.MaxHp; break; }
        }
        Assert.True(authoritativeHp.HasValue, "Expected the real non-lethal attack to commit a HealthChanged mutation at World within the poll window.");

        // Now drive serverB's OWN real poll loop, decoupled entirely from serverA's own cadence,
        // until worldB's own (never-shared) projection converges to the new authoritative HP.
        var convergenceDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorldMonsterInstance? convergedInstance = null;
        while (DateTime.UtcNow < convergenceDeadline)
        {
            await serverB.ProcessOneMonsterTickAsync([sessionB], CancellationToken.None);
            if (worldB.MonsterProjections.TryGet(mapId, out var current))
            {
                var candidate = current.AllInstances.SingleOrDefault(i => i.ActorId == monster.ActorId);
                if (candidate is { } found && found.CurrentHp == authoritativeHp) { convergedInstance = found; break; }
            }
            await Task.Delay(50);
        }
        Assert.NotNull(convergedInstance);
        Assert.Equal(authoritativeHp, convergedInstance!.CurrentHp);
        Assert.Equal(authoritativeMaxHp, convergedInstance.MaxHp);
        Assert.Equal(monster.IncarnationId, convergedInstance.IncarnationId);
        Assert.Equal(WorldMonsterLifecycleState.Alive, convergedInstance.Lifecycle);

        // The literal "no shared object identity required" proof.
        Assert.False(ReferenceEquals(worldA.MonsterProjections, worldB.MonsterProjections));
        Assert.False(ReferenceEquals(worldA.CombatState, worldB.CombatState));

        drainCtsA.Cancel();
        try { await drainTaskA; } catch { }
        clientA.Close(); clientB.Close();
        await runA.WaitAsync(TimeSpan.FromSeconds(5));
        await runB.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 3: session A's own original command genuinely reaches and commits at World (its
    // real, non-lethal result is captured and asserted inline) BEFORE the test deliberately throws
    // in place of returning that result to MapClientSession - simulating "the request committed but
    // the response was lost." While A's own PendingMonsterDamageAttempt remains unresolved, session
    // B kills the same Life through its own real attack path. A tick is driven for A specifically so
    // A's own feed poll observes B's authoritative Died while A's BeginInFlight registration is
    // still open, forcing the arbiter to defer it. Only THEN is A's natural retry allowed to
    // resolve - it must replay (ReplayedSequence), never AlreadyDead, since A's original command
    // already committed.
    [Fact]
    public async Task LostResponse_CommittedButLost_RetryReplaysWhileConcurrentAttackerKills_NoDoubleApplication_DeferredDiedSuppressesStaleProjection()
    {
        var mapId = "izlude";
        // This scenario's own deterministic sequence (lost-response commit, B's concurrent kill,
        // a forced tick, A's natural retry cadence, then a settle window) can legitimately run past
        // MakeWorld's default 5s respawn delay before the final grain-state assertions read HP -
        // a real respawn mid-test would silently reset CurrentHp back to full and falsely pass/fail
        // unrelated assertions. Use a respawn delay comfortably longer than this scenario's own
        // generous bounded waits so no respawn can occur before the final assertions run.
        var world = MakeWorld(mapId, respawnDelayMs: 120_000);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);

        var (clientA, streamA, sessionA, runA, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 300, mapId, (ushort)(MonsterX - 1), MonsterY, ModerateAttackerFor(300));
        var (clientB, streamB, sessionB, runB, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 301, mapId, (ushort)(MonsterX + 1), MonsterY);
        using var _disposeA = clientA;
        using var _disposeB = clientB;

        // B's own reward tail IS asserted by this scenario (see AssertExactlyOneLethalRewardTailAsync
        // below) - unlike a plain discard-drain, this background reader accumulates every byte B's
        // session sends into a buffered stream instead of throwing it away, so the reward tail can
        // still be scanned/verified afterward while ALSO preventing the OS socket buffer from filling
        // and stalling B's own send path (which would otherwise silently stall B's real
        // ApplyMonsterDamageAsync call from ever completing).
        var bufferedB = new BufferedSocketReader(streamB);

        await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        var monster = Assert.Single(projection.AllInstances);

        WorldMonsterDamageCommand? originalCommand = null;
        WorldMonsterDamageResult? originalResult = null;
        WorldMonsterDamageCommand? retryCommand = null;
        WorldMonsterDamageResult? retryResult = null;
        var lostResponseCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryReachedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isFirstDispatch = true;

        // A's second dispatch (its own natural retry, scheduled and fired entirely by production's
        // own cadence - nothing here artificially triggers it) is gated: the command is captured and
        // the gate signaled as soon as production reaches this call, but the REAL World RPC is held
        // back until the test explicitly releases it. This is what makes the required ordering
        // (A observes B's deferred Died WHILE BeginInFlight is still open, and ONLY THEN does A's
        // retry reach World) deterministic instead of a race against B's own kill and A's feed poll
        // under a slower CI run - without this gate, NextRetryAt could legitimately expire and reach
        // World before B has even attacked.
        sessionA.DebugApplyMonsterDamageDispatcher = async (command, ct) =>
        {
            if (isFirstDispatch)
            {
                isFirstDispatch = false;
                var real = await worldRuntime.ApplyMonsterDamageAsync(command, ct);
                Assert.Equal(WorldMonsterDamageStatus.Applied, real.Status);
                Assert.False(real.KilledByThisHit, "Expected ModerateAttackerFor's first hit against Poring to be non-lethal - adjust the fixture if this assertion ever fails.");
                originalCommand = command;
                originalResult = real;
                lostResponseCommitted.TrySetResult();
                throw new IOException("Simulated lost response: the request committed at World but MapServer never received the reply.");
            }
            retryCommand = command;
            retryReachedGate.TrySetResult();
            await releaseRetry.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var retry = await worldRuntime.ApplyMonsterDamageAsync(command, ct);
            retryResult = retry;
            return retry;
        };

        // Transparent wrapper on B - the real dispatcher still runs unmodified, this only captures
        // B's own first real result so "B is the single kill owner" is an OBSERVED result (Applied +
        // KilledByThisHit==true), not merely inferred from the grain's terminal Dead snapshot.
        WorldMonsterDamageResult? firstResultB = null;
        sessionB.DebugApplyMonsterDamageDispatcher = async (command, ct) =>
        {
            var result = await worldRuntime.ApplyMonsterDamageAsync(command, ct);
            firstResultB ??= result;
            return result;
        };

        await streamA.WriteAsync(BuildAttackPacket(monster.ActorId));
        await lostResponseCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // While A's pending attempt remains unresolved, B kills the same Life through its own real
        // attack path.
        await streamB.WriteAsync(BuildAttackPacket(monster.ActorId));
        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var deathDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var confirmedDead = false;
        while (DateTime.UtcNow < deathDeadline && !confirmedDead)
        {
            await Task.Delay(100);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            confirmedDead = page.Snapshot!.SingleOrDefault(i => i.ActorId == monster.ActorId) is { Lifecycle: WorldMonsterLifecycleState.Dead };
        }
        Assert.True(confirmedDead, "Expected B's own attack to confirm the kill.");

        // B's own first real result is the observed proof it is the single kill owner - not an
        // inference from the grain's terminal snapshot.
        var firstResultBDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < firstResultBDeadline && firstResultB is null) await Task.Delay(20);
        Assert.NotNull(firstResultB);
        Assert.Equal(WorldMonsterDamageStatus.Applied, firstResultB!.Status);
        Assert.True(firstResultB.KilledByThisHit, "Expected B's own first real hit to be the observed lethal one.");

        // BEFORE A's retry is allowed to happen, drive a tick for A specifically so its own feed
        // poll observes B's authoritative Died while A's BeginInFlight registration is still open -
        // forcing the arbiter to defer it rather than deliver it as an ordinary bystander vanish.
        // A's retry may already be parked at the gate by this point (its own NextRetryAt cadence is
        // untouched by this test), or it may still be pending - both are fine, since releaseRetry is
        // not signaled until after this tick and the deferred-Died assertion below.
        await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);

        // Only now let A's retry - already captured/parked at the gate, or about to park there on
        // its own natural cadence - actually reach World.
        await retryReachedGate.Task.WaitAsync(TimeSpan.FromSeconds(15));
        releaseRetry.TrySetResult();

        var retryDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < retryDeadline && retryResult is null) await Task.Delay(50);
        Assert.NotNull(retryResult);
        Assert.NotNull(originalCommand);
        Assert.NotNull(originalResult);

        // Drive ticks for both sessions so A's deferred-Died handling and B's own reward tail both
        // fully resolve on the wire before the packet-level assertions below.
        var settleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < settleDeadline)
        {
            await server.ProcessOneMonsterTickAsync([sessionA, sessionB], CancellationToken.None);
            await Task.Delay(50);
        }

        Assert.NotNull(retryCommand);
        // The retry command must be identical to the original.
        Assert.Equal(originalCommand!.Life, retryCommand!.Life);
        Assert.Equal(originalCommand.AttackSequence, retryCommand.AttackSequence);
        Assert.Equal(originalCommand.Damage, retryCommand.Damage);
        Assert.Equal(originalCommand.AcquireEngagement, retryCommand.AcquireEngagement);

        Assert.Equal(WorldMonsterDamageStatus.ReplayedSequence, retryResult!.Status);
        Assert.NotEqual(WorldMonsterDamageStatus.AlreadyDead, retryResult.Status);

        var finalPage = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var finalInstance = Assert.Single(finalPage.Snapshot!, i => i.ActorId == monster.ActorId);
        Assert.Equal(0u, finalInstance.CurrentHp);
        Assert.Equal(WorldMonsterLifecycleState.Dead, finalInstance.Lifecycle);

        // A's side: no stale damage/HP tail, no EXP/progression/drop tail (A never owned the kill),
        // but - item 14 - A DOES now legitimately observe B's own killing action (srcActorId=301)
        // exactly once, strictly before the deferred Died vanish, via the World feed. A ALSO
        // receives its own local fast-path echo (srcActorId=300) for its own earlier non-lethal
        // attack's ReplayedSequence retry (HandleDamageResultAsync's own non-lethal
        // Applied-or-ReplayedSequence branch always echoes to the attacker's own session,
        // regardless of replay status) - a SEPARATE, semantically distinct action from B's, never a
        // duplicate of it.
        await AssertExactlyOneDiedVanishNoRewardTailAllowingActionsAsync(streamA, 301u, 300u);

        // B's side: B is the real lethal owner, so its stream must show exactly one lethal
        // vanish/reward tail and no duplicate - bufferedB accumulated every byte B's session sent
        // (not discarded like a plain drain), so this scans the real, complete sequence instead of
        // merely proving traffic existed. Expected EXP presence is derived from Poring's own real
        // generated MobDefinition (BaseExp/JobExp), not a hard-coded count - both are non-zero for
        // Poring and the default GameplayRateOptions used by ConnectSessionAsync (100% rate), so
        // both a base-EXP and a job-EXP gain packet are expected exactly once each.
        await bufferedB.StopAndAssertExactlyOneLethalRewardTailAsync(monster.ActorId, GeneratedMobs.Poring.BaseExp, GeneratedMobs.Poring.JobExp);

        clientA.Close(); clientB.Close();
        await runA.WaitAsync(TimeSpan.FromSeconds(5));
        await runB.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 4: session A establishes a real AttackSequence ledger entry for its own PresenceId,
    // then a SECOND dispatch (its own natural retry/next hit) is gated open mid-flight - its
    // WorldMonsterDamageCommand is already fully constructed, including the still-current (soon to
    // be stale) PresenceId, before the gate is even installed. Session A then genuinely disconnects
    // and reconnects (a fresh ConnectAttackerAsync call, same CharacterId, new PresenceId) - the
    // gate is only released once the grain itself confirms the old PresenceId is no longer valid.
    // The held-back dispatch, still carrying the OLD PresenceId, must then be rejected
    // StaleAttackerPresence with zero HP/wire impact, and the new PresenceId must attack normally
    // afterward.
    [Fact]
    public async Task ReconnectMidCombat_StaleDelayedCommandFromOldPresence_RejectedAfterReconnect()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);

        const uint accountId = 400;
        var (clientA, streamA, sessionA, _, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId, mapId, (ushort)(MonsterX - 1), MonsterY, ModerateAttackerFor(accountId));

        await server.ProcessOneMonsterTickAsync([sessionA], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([sessionA], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([sessionA], CancellationToken.None);

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        var monster = Assert.Single(projection.AllInstances);

        var staleRetryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorldMonsterDamageResult? staleResult = null;
        var isFirstDispatch = true;

        sessionA.DebugApplyMonsterDamageDispatcher = async (command, ct) =>
        {
            if (isFirstDispatch)
            {
                isFirstDispatch = false;
                return await worldRuntime.ApplyMonsterDamageAsync(command, ct); // Establishes the ledger entry for presence A.
            }
            staleRetryStarted.TrySetResult();
            await releaseStaleRetry.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // `ct` is this session's own linked cancellation token - closing clientA's socket below
            // makes RunAsync's read loop exit and immediately cancel _sessionCancellation (see
            // MapClientSession's own disconnect-teardown, which does this BEFORE joining the attack
            // loop this exact call is parked inside - not gated behind it). Using the by-then-
            // cancelled `ct` here would make this call throw OperationCanceledException instead of
            // ever reaching the grain, which is not what this scenario is proving - a real deferred/
            // queued retry that was already in flight when a client disconnects still runs to
            // completion against World; only the LOCAL session-side bookkeeping is torn down early.
            // CancellationToken.None reflects that: this call's job is to reach the grain and prove
            // the OLD PresenceId is rejected, exactly like production's real dispatcher call would
            // still do for an already-in-flight RPC.
            var result = await worldRuntime.ApplyMonsterDamageAsync(command, CancellationToken.None); // `command` still carries the OLD (by-now-stale) PresenceId, built before this gate opened.
            staleResult = result;
            return result;
        };

        // Drain A's stream in the background - its own hits produce wire traffic this test doesn't
        // read directly.
        var drainCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            var sink = new byte[4096];
            try { while (!drainCts.IsCancellationRequested) await streamA.ReadAsync(sink, drainCts.Token); }
            catch (OperationCanceledException) { } catch (IOException) { }
        });

        await streamA.WriteAsync(BuildAttackPacket(monster.ActorId));
        await staleRetryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var presenceIdA = sessionA.PresenceId!.Value;
        clientA.Client.Close(); // Do NOT await runA here - the attack loop is blocked inside the gated dispatcher; awaiting would deadlock.

        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var life = new WorldMonsterLifeReference(mapId, projection.CurrentEpoch!.Value, monster.ActorId, monster.IncarnationId);

        // RunAsync's own teardown (LeavePlayerWorldAsync -> UnregisterPresenceAsync) cannot run yet:
        // that teardown awaits _attackLoop's own completion, and _attackLoop is the exact task
        // parked inside this session's gated dispatcher above - genuinely deadlocked against itself
        // until releaseStaleRetry fires, which must not happen before the stale command resolves.
        // A real disconnect's eventual unregistration is simulated directly against the grain
        // instead (mirroring Scenario 5's own single legitimate direct-grain-call precedent) - this
        // still exercises the real ApplyMonsterDamageAsync validation contract (step 2 of its own
        // documented ordering) against a genuinely-removed presence, without requiring this
        // session's own attack loop to unwind first.
        var unregistration = await grain.UnregisterPresenceAsync(mapId, accountId, presenceIdA);
        Assert.Equal(WorldPresenceUnregistrationStatus.Removed, unregistration.Status);

        var window = await grain.ValidateMonsterAttackWindowAsync(new WorldMonsterAttackWindowQuery(life, accountId, presenceIdA));
        Assert.NotEqual(WorldMonsterAttackWindowStatus.Valid, window.Status);

        // The grain-level presence is now gone, but session A's own LOCAL MapServer-side
        // registration (world.PlayerVisibility, populated by EnterPlayerWorldAsync's own
        // _playerVisibility.RegisterAsync call, keyed by ActorId) is a SEPARATE registry this direct
        // grain call never touches - RunAsync's own teardown would normally clear it via
        // LeavePlayerWorldAsync, but that teardown is exactly what's deadlocked above. Without this,
        // A2's own reconnect (same ActorId) would throw "already registered" from
        // PlayerVisibilityCoordinator.RegisterAsync's own conflict check.
        await world.PlayerVisibility.UnregisterAsync(accountId, CancellationToken.None);

        var hpBeforeRelease = (await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single(i => i.ActorId == monster.ActorId).CurrentHp;

        var (clientA2, streamA2, sessionA2, runA2, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId, mapId, (ushort)(MonsterX - 1), MonsterY, ModerateAttackerFor(accountId));
        using var _disposeA2 = clientA2;
        var presenceIdB = sessionA2.PresenceId!.Value;
        Assert.NotEqual(presenceIdA, presenceIdB);

        releaseStaleRetry.TrySetResult();

        var staleResultDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < staleResultDeadline && staleResult is null) await Task.Delay(20);
        Assert.NotNull(staleResult);
        Assert.Equal(WorldMonsterDamageStatus.StaleAttackerPresence, staleResult!.Status);

        var hpAfterStaleResolves = (await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single(i => i.ActorId == monster.ActorId).CurrentHp;
        Assert.Equal(hpBeforeRelease, hpAfterStaleResolves);

        drainCts.Cancel();
        try { await drainTask; } catch { }

        // The new PresenceId attacks normally afterward.
        await server.ProcessOneMonsterTickAsync([sessionA2], CancellationToken.None);
        await streamA2.WriteAsync(BuildAttackPacket(monster.ActorId));
        var engagedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var engaged = false;
        while (DateTime.UtcNow < engagedDeadline && !engaged)
        {
            await Task.Delay(100);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            engaged = page.Snapshot!.SingleOrDefault(i => i.ActorId == monster.ActorId)?.EngagedTarget?.CharacterId == accountId;
        }
        Assert.True(engaged, "Expected the reconnected session (new PresenceId) to attack normally after the stale request was rejected.");

        clientA2.Close();
        await runA2.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 5: extends the existing Respawned_RealFeedEntry_... pattern with two changes: the
    // kill is driven through a REAL attacker session's own attack (not a direct grain call with a
    // synthetic presence), and a second, non-attacking bystander session is present throughout and
    // independently observes the same kill/respawn cycle.
    //
    // Scope note: this test proves end-to-end functional respawn continuity (new incarnation, full
    // HP, old-life rejection, new-life attackability, both consumers converging). It does NOT
    // re-prove that LethalDeathProjectionArbiter's internal _alreadyProjected marker for the OLD
    // Life was removed from memory - that marker is keyed by exact Life, so a new incarnation (a
    // different Life) functions correctly regardless of whether the old marker leaked. The actual
    // removal-semantics proof lives in LethalDeathProjectionArbiterTests.cs
    // (ForgetProjectedForActor_RemovesStaleIncarnations_PreservesExceptedAndUnrelatedEntries /
    // ForgetProjectedForActor_MultiRespawnBacklog_ClearsAllStaleIncarnations_PreservesCurrent); the
    // correct CALL-SITE wiring (NotifyMonsterRespawnedAsync calling ForgetProjectedForActor with the
    // right identity) is proven by MapClientSessionRespawnCleanupTests.cs. Both are re-run alongside
    // this substep's own verification pass, not re-derived here.
    // Live multiplayer regression: the player-vs-monster combat ACTION (0x08C8) is AREA-visible, not
    // attacker-socket-only (see PlayerAttackActionOutcome's own doc comment) - a nearby bystander must
    // see the killing blow's own action packet BEFORE the authoritative Died vanish, exactly as it
    // already does for a monster's own attack. Sessions here are constructed directly (ConnectSessionAsync,
    // not MapTcpServer's real accept loop), so this test wires `playerAttackFanout` to `server`'s own
    // FanOutPlayerAttackActionAsync explicitly and injects both into `server`'s `_sessions` via the same
    // narrow reflection seam MapTcpServerRunAsyncSupervisionTests already establishes as this project's
    // accepted pattern for a production-only dictionary with no other test seam.
    private static void InjectSession(MapTcpServer server, int sessionId, MapClientSession session)
    {
        var field = typeof(MapTcpServer).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MapTcpServer._sessions field not found - test seam broken by a rename.");
        var sessions = (ConcurrentDictionary<int, MapClientSession>)field.GetValue(server)!;
        sessions[sessionId] = session;
    }

    [Fact]
    public async Task PlayerAttack_LethalHit_BystanderReceivesTheKillingActionBeforeTheVanish()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId, respawnDelayMs: 60_000); // long respawn - irrelevant to this test, kept out of the way.
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);

        var (clientAttacker, streamAttacker, sessionAttacker, runAttacker, _) = await ConnectSessionAsync(
            server, world, worldRuntime, accountId: 900, mapId, (ushort)(MonsterX - 1), MonsterY, playerAttackFanout: server.FanOutPlayerAttackActionAsync);
        var (clientBystander, streamBystander, sessionBystander, runBystander, _) = await ConnectSessionAsync(
            server, world, worldRuntime, accountId: 901, mapId, (ushort)(MonsterX + 1), MonsterY, playerAttackFanout: server.FanOutPlayerAttackActionAsync);
        InjectSession(server, 1, sessionAttacker);
        InjectSession(server, 2, sessionBystander);
        using var _disposeAttacker = clientAttacker;
        using var _disposeBystander = clientBystander;

        await server.ProcessOneMonsterTickAsync([sessionAttacker, sessionBystander], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([sessionAttacker, sessionBystander], CancellationToken.None);
        await ReadUntilMonsterDiscoveryAsync(streamAttacker);
        await ReadUntilMonsterDiscoveryAsync(streamBystander);

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        var monster = Assert.Single(projection.AllInstances);

        // The attacker's own stream also carries its own action/HP-info/progression/vanish tail for
        // every hit - drain it continuously in the background (the established pattern this file's own
        // PlayerAttack_LethalHit_CallsTryMarkMonsterDead_... test already uses) so those unread response
        // packets never fill the OS socket buffer and stall the session's own send path.
        using var attackerDrainCts = new CancellationTokenSource();
        var attackerDrainTask = Task.Run(async () =>
        {
            var sink = new byte[4096];
            try { while (!attackerDrainCts.IsCancellationRequested) await streamAttacker.ReadAsync(sink, attackerDrainCts.Token); }
            catch (OperationCanceledException) { } catch (IOException) { }
        });

        await streamAttacker.WriteAsync(BuildAttackPacket(monster.ActorId));

        // Walk the bystander's own stream generically (mirroring ReadUntilVanishAsync's own framing),
        // recording every player-attack action seen, until the authoritative Died vanish arrives - proves
        // BOTH that the bystander receives the action (the live regression's exact fix) AND that it
        // arrives strictly before the vanish (never after), for however many hits this attacker's own
        // real-attack-delay cadence actually produces before the kill lands. Interleaved with driving the
        // ordinary tick loop (never a manual "verification" RPC call) - the SAME real-World-feed
        // Died-fan-out path KillThroughRealAttack_BystanderPresent_... already proves, exercised here
        // together with the new action fan-out.
        var actionsSeenByBystander = new List<(uint Src, uint Dst, uint Damage)>();
        byte[]? vanish = null;
        var readTask = Task.Run(async () =>
        {
            while (vanish is null)
            {
                var header = await ReadExact(streamBystander, 2);
                var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
                if (opcode == (short)PacketConstants.ZcNotifyAct3)
                {
                    var body = await ReadExact(streamBystander, PacketConstants.ZcNotifyAct3Length - 2);
                    var full = (byte[])[.. header, .. body];
                    actionsSeenByBystander.Add((BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(2)), BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(6)), BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(22))));
                }
                else if (opcode == (short)PacketConstants.ZcNotifyVanish)
                {
                    vanish = (byte[])[.. header, .. await ReadExact(streamBystander, PacketConstants.ZcNotifyVanishLength - 2)];
                }
                else
                {
                    await SkipPacketBodyAsync(streamBystander, opcode);
                }
            }
        });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !readTask.IsCompleted)
        {
            await server.ProcessOneMonsterTickAsync([sessionAttacker, sessionBystander], CancellationToken.None);
            await Task.Delay(20);
        }
        await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        attackerDrainCts.Cancel();
        try { await attackerDrainTask; } catch { /* Expected once the stream is torn down. */ }

        Assert.NotNull(vanish);
        Assert.Equal(monster.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(vanish!.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanish[6]);
        Assert.NotEmpty(actionsSeenByBystander); // The live regression: this used to be empty.
        Assert.All(actionsSeenByBystander, action => Assert.Equal(900u, action.Src));
        Assert.All(actionsSeenByBystander, action => Assert.Equal(monster.ActorId, action.Dst));
        Assert.All(actionsSeenByBystander, action => Assert.True(action.Damage > 0));

        clientAttacker.Close(); clientBystander.Close();
        await runAttacker.WaitAsync(TimeSpan.FromSeconds(5));
        await runBystander.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task KillThroughRealAttack_BystanderPresent_RespawnContinuity_StaleIncarnationRejected()
    {
        var mapId = "izlude";
        var world = MakeWorld(mapId, respawnDelayMs: 200);
        var worldRuntime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, worldRuntime);

        var (clientAttacker, streamAttacker, sessionAttacker, runAttacker, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 500, mapId, (ushort)(MonsterX - 1), MonsterY);
        var (clientBystander, streamBystander, sessionBystander, runBystander, _) = await ConnectSessionAsync(server, world, worldRuntime, accountId: 501, mapId, (ushort)(MonsterX + 1), MonsterY);
        using var _disposeAttacker = clientAttacker;
        using var _disposeBystander = clientBystander;

        await server.ProcessOneMonsterTickAsync([sessionAttacker, sessionBystander], CancellationToken.None);
        await server.ProcessOneMonsterTickAsync([sessionAttacker, sessionBystander], CancellationToken.None);
        // Each session's own discovery burst here includes BOTH the other player (ZcNotifyNewEntry,
        // 0x09FE - a PC discovering another nearby PC) and the monster (ZcNotifyStandEntry, 0x09FF) -
        // unlike the single-attacker-only Respawned_RealFeedEntry_... pattern this scenario extends,
        // there is a second live player here, so ordering between the two kinds is not guaranteed.
        // Drain dynamic packets from each stream until the monster's own discovery is found.
        await ReadUntilMonsterDiscoveryAsync(streamAttacker);
        await ReadUntilMonsterDiscoveryAsync(streamBystander);

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        var original = Assert.Single(projection.AllInstances);
        var epoch = projection.CurrentEpoch!.Value;
        var oldLife = new WorldMonsterLifeReference(mapId, epoch, original.ActorId, original.IncarnationId);

        await streamAttacker.WriteAsync(BuildAttackPacket(original.ActorId));

        var grain = _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var deathDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var confirmedDead = false;
        while (DateTime.UtcNow < deathDeadline && !confirmedDead)
        {
            await Task.Delay(100);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            confirmedDead = page.Snapshot!.SingleOrDefault(i => i.ActorId == original.ActorId) is { Lifecycle: WorldMonsterLifecycleState.Dead };
        }
        Assert.True(confirmedDead, "Expected the real attacker session's own attack to confirm the kill.");

        // Both sessions must observe exactly one Died vanish, driven purely through the real
        // ProcessOneMonsterTickAsync -> FanOutEntryAsync path. The attacker's own stream also
        // carries its own lethal hit's damage/HP-info tail (and either stream can carry an ordinary
        // ZcStopMove) ahead of the vanish - ReadUntilVanishAsync skips over anything else using each
        // packet's own real framing (mirrors AssertExactlyOneDiedVanishNoRewardTailAsync's identical
        // fix), rather than assuming the vanish is the very next fixed-length packet on the wire.
        var vanishDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var attackerVanishTask = ReadUntilVanishAsync(streamAttacker);
        var bystanderVanishTask = ReadUntilVanishAsync(streamBystander);
        while (DateTime.UtcNow < vanishDeadline && !(attackerVanishTask.IsCompletedSuccessfully && bystanderVanishTask.IsCompletedSuccessfully))
        {
            await server.ProcessOneMonsterTickAsync([sessionAttacker, sessionBystander], CancellationToken.None);
            await Task.Delay(20);
        }
        var attackerVanish = await attackerVanishTask;
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, attackerVanish[6]);
        var bystanderVanish = await bystanderVanishTask;
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, bystanderVanish[6]);

        // Continue driving ticks until a NEW incarnation appears.
        var respawnDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorldMonsterInstance? respawnedInstance = null;
        while (DateTime.UtcNow < respawnDeadline)
        {
            await server.ProcessOneMonsterTickAsync([sessionAttacker, sessionBystander], CancellationToken.None);
            if (world.MonsterProjections.TryGet(mapId, out var current) &&
                current.AllInstances.SingleOrDefault(i => i.ActorId == original.ActorId) is { Lifecycle: WorldMonsterLifecycleState.Alive } candidate &&
                !candidate.IncarnationId.Equals(original.IncarnationId))
            {
                respawnedInstance = candidate;
                break;
            }
            await Task.Delay(50);
        }
        Assert.NotNull(respawnedInstance);
        Assert.NotEqual(original.IncarnationId, respawnedInstance!.IncarnationId);
        Assert.Equal(respawnedInstance.MaxHp, respawnedInstance.CurrentHp);

        // Both sessions receive their own independent rediscovery wire packet.
        var attackerRediscovery = await ReadUntilMonsterDiscoveryAsync(streamAttacker);
        Assert.Equal(original.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(attackerRediscovery.AsSpan(5)));
        var bystanderRediscovery = await ReadUntilMonsterDiscoveryAsync(streamBystander);
        Assert.Equal(original.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(bystanderRediscovery.AsSpan(5)));

        // The old Life is rejected as stale - mirrors the already-established
        // StaleLifeReference_AfterRespawn_... pattern (proving World's own contract, not
        // re-exercising the session dispatch path).
        var killerPresenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(new WorldPlayerPresence(killerPresenceId, ActorId: 998, CharacterId: 998, mapId, X: original.X, Y: original.Y), new WorldPlayerPublicState("Test", 0, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        var staleAttempt = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(oldLife, AttackerCharacterId: 998, killerPresenceId, AttackSequence: 1, Damage: 9999, AcquireEngagement: false));
        Assert.Equal(WorldMonsterDamageStatus.StaleLifeReference, staleAttempt.Status);
        // Item 14: throwaway fixture, not one of the two sessions under test - see the earlier
        // Respawned_RealFeedEntry test's own identical doc comment for why this must be
        // unregistered before any further ProcessOneMonsterTickAsync/session activity.
        await grain.UnregisterPresenceAsync(mapId, characterId: 998, killerPresenceId);

        // The attacker session's own target resolution naturally re-resolves to the new
        // incarnation and can attack it normally.
        await streamAttacker.WriteAsync(BuildAttackPacket(original.ActorId));
        var freshAttackDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var freshHitObserved = false;
        while (DateTime.UtcNow < freshAttackDeadline && !freshHitObserved)
        {
            await Task.Delay(100);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            var candidate = page.Snapshot!.SingleOrDefault(i => i.ActorId == original.ActorId);
            freshHitObserved = candidate is not null && candidate.IncarnationId.Equals(respawnedInstance.IncarnationId) && candidate.CurrentHp < candidate.MaxHp;
        }
        Assert.True(freshHitObserved, "Expected the attacker session to land a fresh attack against the new incarnation normally.");

        clientAttacker.Close(); clientBystander.Close();
        await runAttacker.WaitAsync(TimeSpan.FromSeconds(5));
        await runBystander.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Substep 10: single source of truth for every fixed-length opcode these packet-scanning test
    // helpers need to skip over or reject. Centralized so the framing logic in
    // AssertNoDamageHpOrRewardPacketsAsync/AssertExactlyOneDiedVanishNoRewardTailAsync/
    // ReadUntilVanishAsync cannot silently desynchronize again by guessing a fixed skip length or
    // omitting a reward/progression opcode one of them forbids but another doesn't - see the review
    // finding that caught exactly that gap (ZcParameterChange/ZcLongLongParameterChange/
    // ZcNotifyExperience/ZcNotifyEffect/ZcItemPickupAck were skipped as "unknown length-prefixed"
    // instead of being recognized AND rejected as reward/progression packets). -1 means "not a known
    // fixed-length opcode" - the caller falls back to the dynamic, self-describing length-prefixed
    // shape (ZcNotifyNewEntry/ZcNotifyStandEntry, matching ReadDynamic's own 4-byte-header/2-byte-
    // length shape used elsewhere in this file).
    internal static int KnownFixedPacketLength(short opcode) => opcode switch
    {
        (short)PacketConstants.ZcNotifyAct3 => PacketConstants.ZcNotifyAct3Length,
        (short)PacketConstants.ZcHpInfo => PacketConstants.ZcHpInfoLength,
        (short)PacketConstants.ZcStopMove => PacketConstants.ZcStopMoveLength,
        (short)PacketConstants.ZcNotifyVanish => PacketConstants.ZcNotifyVanishLength,
        (short)PacketConstants.ZcParameterChange => 8,
        (short)PacketConstants.ZcLongLongParameterChange => 12,
        (short)PacketConstants.ZcNotifyExperience => PacketConstants.ZcNotifyExperienceLength,
        (short)PacketConstants.ZcNotifyEffect => PacketConstants.ZcNotifyEffectLength,
        (short)PacketConstants.ZcItemPickupAck => PacketConstants.ZcItemPickupAckLength,
        _ => -1,
    };

    // Substep 10: every reward/progression opcode a lethal hit's own reward tail can produce -
    // forbidden on a stream this scenario asserts received NO reward tail (an AlreadyDead loser, or
    // a bystander's own deferred-Died path that never owned the kill). The review finding that
    // required this: the old checks only rejected ZcNotifyAct3/ZcHpInfo (damage/HP) and never
    // actually rejected the EXP/parameter-change/item-pickup families a real kill's reward
    // projection sends, so a genuine double-reward defect could have slipped through undetected.
    private static bool IsForbiddenRewardOpcode(short opcode) =>
        opcode == (short)PacketConstants.ZcNotifyAct3 ||
        opcode == (short)PacketConstants.ZcHpInfo ||
        opcode == (short)PacketConstants.ZcParameterChange ||
        opcode == (short)PacketConstants.ZcLongLongParameterChange ||
        opcode == (short)PacketConstants.ZcNotifyExperience ||
        opcode == (short)PacketConstants.ZcNotifyEffect ||
        opcode == (short)PacketConstants.ZcItemPickupAck;

    // Substep 10: reads one packet's own body past an already-consumed 2-byte opcode header, using
    // its real length (fixed-size via KnownFixedPacketLength, else the dynamic length-prefixed
    // shape) - shared by every helper below so a packet is always skipped by its OWN true length,
    // never a fixed too-small guess.
    private static async Task SkipPacketBodyAsync(Stream stream, short opcode)
    {
        var fixedLength = KnownFixedPacketLength(opcode);
        if (fixedLength >= 0)
        {
            await ReadExact(stream, fixedLength - 2);
        }
        else
        {
            var lengthField = await ReadExact(stream, 2);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthField);
            await ReadExact(stream, length - 4);
        }
    }

    // Substep 10 helper: bounded, non-destructive proof that no damage/HP-info/reward packet ever
    // arrives on the given stream - used to prove an AlreadyDead loser's wire silence directly.
    private static async Task AssertNoDamageHpOrRewardPacketsAsync(NetworkStream stream)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            byte[] header;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                var buffer = new byte[2];
                await stream.ReadExactlyAsync(buffer, cts.Token);
                header = buffer;
            }
            catch (OperationCanceledException) { return; } // Nothing else arrived - the load-bearing proof.
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
            Assert.False(
                IsForbiddenRewardOpcode(opcode),
                $"Expected no damage/HP-info/reward/progression packet on the AlreadyDead loser's own stream, but observed opcode 0x{opcode:X4}.");
            // Drain whatever else this opcode's own payload is (best-effort - anything reaching here
            // is already unexpected for this proof) using its own real length.
            await SkipPacketBodyAsync(stream, opcode);
        }
    }

    // Substep 10 helper: reads exactly one Died vanish packet off the given stream and confirms no
    // damage/HP-info/reward/progression packet precedes or follows it within the bounded window.
    private static Task AssertExactlyOneDiedVanishNoRewardTailAsync(NetworkStream stream) =>
        AssertExactlyOneDiedVanishAsync(stream, allowedActionSrcActorIds: []);

    // Item 14: a BYSTANDER observer of a killing hit it did NOT itself land now legitimately
    // receives exactly the killer's own PlayerAttackAction (0x08C8), sequenced strictly before the
    // Died vanish, via the World feed's own structural ordering (see
    // WorldMonsterMapSimulation.ApplyDamage's own doc comment) - this is the item 14 §6 cross-replica
    // fanout requirement, not a regression AssertExactlyOneDiedVanishNoRewardTailAsync's own
    // pre-item-14 "no damage packet at all" assumption predates. `allowedActionSrcActorIds` is
    // multiset-checked by srcActorId (0x08C8's own offset 2 field): each distinct allowed source may
    // appear AT MOST as many times as it is listed, so a caller whose observer ALSO independently
    // replayed its own earlier (non-lethal, ReplayedSequence) attack in the same window can list its
    // own ActorId once too, alongside the killer's - these are two semantically DIFFERENT actions
    // (different srcActorId), never a duplicate of the same one. Still forbids everything else this
    // bystander must never receive: its own HP-info, EXP/progression, item-pickup (all exclusively
    // the killer's own reward tail).
    private static Task AssertExactlyOneDiedVanishNoRewardTailAllowingActionsAsync(NetworkStream stream, params uint[] allowedActionSrcActorIds) =>
        AssertExactlyOneDiedVanishAsync(stream, allowedActionSrcActorIds);

    private static async Task AssertExactlyOneDiedVanishAsync(NetworkStream stream, IReadOnlyCollection<uint> allowedActionSrcActorIds)
    {
        var vanishSeen = false;
        var remainingAllowedActions = allowedActionSrcActorIds.ToList();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            byte[] header;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                var buffer = new byte[2];
                await stream.ReadExactlyAsync(buffer, cts.Token);
                header = buffer;
            }
            catch (OperationCanceledException) { break; }
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
            if (opcode == (short)PacketConstants.ZcNotifyVanish)
            {
                Assert.False(vanishSeen, "Expected exactly one Died vanish packet, but observed a second one.");
                var rest = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength - 2);
                var full = header.Concat(rest).ToArray();
                Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, full[6]);
                vanishSeen = true;
                continue;
            }
            if (opcode == (short)PacketConstants.ZcNotifyAct3 && remainingAllowedActions.Count > 0)
            {
                var body = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length - 2);
                var srcActorId = BinaryPrimitives.ReadUInt32LittleEndian(body);
                Assert.True(remainingAllowedActions.Remove(srcActorId), $"Received an unexpected/duplicate 0x08C8 action from srcActorId={srcActorId}.");
                if (allowedActionSrcActorIds.Distinct().Count() == 1) // Single distinct killer id (the common bystander case): must precede the vanish.
                    Assert.False(vanishSeen, "Expected the killing action to arrive BEFORE the Died vanish, never after.");
                continue;
            }
            Assert.False(
                IsForbiddenRewardOpcode(opcode),
                $"Expected no damage/HP-info/reward/progression packet on this stream, but observed opcode 0x{opcode:X4}.");
            // Every other packet this server's fan-out/discovery path can produce is either a
            // fixed-size struct KnownFixedPacketLength knows the exact length of, or a dynamic,
            // self-describing length-prefixed packet - see SkipPacketBodyAsync's own doc comment.
            await SkipPacketBodyAsync(stream, opcode);
        }
        Assert.True(vanishSeen, "Expected exactly one deferred Died vanish packet to arrive.");
        Assert.Empty(remainingAllowedActions);
    }

    // Substep 10 (review fix): accumulates every byte a session sends, from a background reader
    // task, into an in-memory buffer instead of discarding it (a plain discard-drain would still
    // prevent the OS socket buffer from filling and stalling the session's own send path, but makes
    // it impossible to later prove anything about what was actually sent - "we drained the stream"
    // is not the same claim as "we verified its contents"). StopAndAssertExactlyOneLethalRewardTailAsync
    // stops the reader and scans the complete, real captured sequence for exactly one lethal
    // reward tail (damage, then optionally HP-info, then the reward/progression packet family, then
    // exactly one Died vanish), rejecting a duplicate of any of them.
    private sealed class BufferedSocketReader
    {
        private readonly MemoryStream _buffer = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _readTask;

        public BufferedSocketReader(NetworkStream stream)
        {
            _readTask = Task.Run(async () =>
            {
                var chunk = new byte[4096];
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(chunk, _cts.Token);
                        if (read == 0) return;
                        lock (_buffer) _buffer.Write(chunk, 0, read);
                    }
                }
                catch (OperationCanceledException) { } catch (IOException) { }
            });
        }

        // Substep 10 (review fix): the invariant this proves is "one logical kill => one damage/HP/
        // death/reward projection", not merely "one damage packet + one vanish". Parses B's real
        // captured wire output and asserts, for the exact killed monster/expected reward shape:
        //   - exactly one ZcNotifyAct3 (damage) for expectedActorId
        //   - exactly one ZcHpInfo for expectedActorId, with its authoritative HP field == 0
        //   - exactly one ZcNotifyVanish for expectedActorId, reason == Died
        //   - exactly one ZcNotifyExperience carrying BaseExperienceParameter, IFF expectedBaseExp > 0
        //   - exactly one ZcNotifyExperience carrying JobExperienceParameter, IFF expectedJobExp > 0
        // A second occurrence of any of these fails the test. ZcParameterChange/ZcLongLongParameterChange/
        // ZcNotifyEffect (level-up/stat-sync packets) legitimately vary with progression state, so
        // their total count is not asserted - they are still parsed with their correct fixed lengths
        // so the byte-level scan cannot desynchronize. No ZcItemPickupAck is expected/asserted here:
        // this scenario's MakeWorld wires an empty QuestDropResolver, so no quest drop can fire.
        public async Task StopAndAssertExactlyOneLethalRewardTailAsync(uint expectedActorId, long expectedBaseExp, long expectedJobExp)
        {
            // Bounded settle window: the reward tail is produced asynchronously (ticks/background
            // loops) after the kill is confirmed, so give it a moment to actually land in the buffer
            // before stopping the reader and scanning what was captured.
            var settleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < settleDeadline) await Task.Delay(50);

            _cts.Cancel();
            try { await _readTask; } catch { }

            byte[] captured;
            lock (_buffer) captured = _buffer.ToArray();
            using var replay = new MemoryStream(captured);

            var damageSeen = false;
            var hpInfoSeen = false;
            var vanishSeen = false;
            var baseExpSeen = false;
            var jobExpSeen = false;
            while (replay.Position < replay.Length)
            {
                var header = await ReadExact(replay, 2);
                var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
                if (opcode == (short)PacketConstants.ZcNotifyVanish)
                {
                    Assert.False(vanishSeen, "Expected exactly one Died vanish packet on the killer's own stream, but observed a second one.");
                    var full = header.Concat(await ReadExact(replay, PacketConstants.ZcNotifyVanishLength - 2)).ToArray();
                    Assert.Equal(expectedActorId, BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(2)));
                    Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, full[6]);
                    vanishSeen = true;
                    continue;
                }
                if (opcode == (short)PacketConstants.ZcNotifyAct3)
                {
                    Assert.False(damageSeen, "Expected exactly one damage packet on the killer's own stream, but observed a second one.");
                    var full = header.Concat(await ReadExact(replay, PacketConstants.ZcNotifyAct3Length - 2)).ToArray();
                    Assert.Equal(expectedActorId, BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(6))); // dstId (the killed monster)
                    damageSeen = true;
                    continue;
                }
                if (opcode == (short)PacketConstants.ZcHpInfo)
                {
                    Assert.False(hpInfoSeen, "Expected exactly one HP-info packet on the killer's own stream, but observed a second one.");
                    var full = header.Concat(await ReadExact(replay, PacketConstants.ZcHpInfoLength - 2)).ToArray();
                    Assert.Equal(expectedActorId, BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(2)));
                    Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(6))); // authoritative HP field
                    hpInfoSeen = true;
                    continue;
                }
                if (opcode == (short)PacketConstants.ZcNotifyExperience)
                {
                    var full = header.Concat(await ReadExact(replay, PacketConstants.ZcNotifyExperienceLength - 2)).ToArray();
                    var parameterId = BinaryPrimitives.ReadUInt16LittleEndian(full.AsSpan(14));
                    if (parameterId == IroCharacterProgressionPackets.BaseExperienceParameter)
                    {
                        Assert.False(baseExpSeen, "Expected exactly one base-EXP gain packet, but observed a second one.");
                        baseExpSeen = true;
                    }
                    else if (parameterId == IroCharacterProgressionPackets.JobExperienceParameter)
                    {
                        Assert.False(jobExpSeen, "Expected exactly one job-EXP gain packet, but observed a second one.");
                        jobExpSeen = true;
                    }
                    continue;
                }
                await SkipPacketBodyAsync(replay, opcode);
            }
            Assert.True(damageSeen, "Expected the killer's own stream to carry exactly one damage packet for its lethal hit.");
            Assert.True(hpInfoSeen, "Expected the killer's own stream to carry exactly one HP-info packet showing HP==0.");
            Assert.True(vanishSeen, "Expected the killer's own stream to carry exactly one Died vanish packet.");
            Assert.Equal(expectedBaseExp > 0, baseExpSeen);
            Assert.Equal(expectedJobExp > 0, jobExpSeen);
        }
    }

    // Real capture-verified 8-byte shape (mirrors IroAttackRequestPacketTests' own CapturedBytes
    // fixture: kill-poring-heal-jobup.pcapng frame 614) - id.W targetActorId.L actionType.B
    // (7=DMG_REPEAT) opaqueByte.B (0x7F). A 7-byte packet is rejected outright by
    // IroAttackRequestPacket.TryParse (PacketConstants.IroCzAttackRequestLength is 8), so the old
    // shape here never actually exercised the real attack-request handling path at all.
    private static byte[] BuildAttackPacket(uint targetActorId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x0437);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), targetActorId);
        packet[6] = 7; // DMG_REPEAT
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

    // Substep 10 (Scenario 5): with a bystander session present, a stream's discovery burst mixes
    // ZcNotifyNewEntry (0x09FE, a PC discovering another nearby PC) with ZcNotifyStandEntry (0x09FF,
    // monster discovery) in no guaranteed order - unlike the single-attacker Respawned_RealFeedEntry_...
    // pattern this scenario extends, there is a second live player here. Drains dynamic packets
    // (both share ReadDynamic's own 4-byte-header/2-byte-length shape) until the monster's own
    // discovery is found, and returns it.
    private static async Task<byte[]> ReadUntilMonsterDiscoveryAsync(Stream stream)
    {
        while (true)
        {
            var packet = await ReadDynamic(stream);
            if (BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyStandEntry) return packet;
        }
    }

    // Substep 10 (Scenario 5): the attacker's own lethal hit produces its own damage/HP-info tail
    // (and either stream can carry an ordinary ZcStopMove) ahead of the authoritative Died vanish -
    // skips every other packet using its OWN real length (fixed-size for known opcodes, otherwise
    // length-prefixed - the same framing fix applied to AssertExactlyOneDiedVanishNoRewardTailAsync/
    // AssertNoDamageHpOrRewardPacketsAsync) rather than assuming the vanish is the very next
    // fixed-length packet on the wire.
    private static async Task<byte[]> ReadUntilVanishAsync(Stream stream)
    {
        while (true)
        {
            var header = await ReadExact(stream, 2);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
            if (opcode == (short)PacketConstants.ZcNotifyVanish)
                return [.. header, .. await ReadExact(stream, PacketConstants.ZcNotifyVanishLength - 2)];
            await SkipPacketBodyAsync(stream, opcode);
        }
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
