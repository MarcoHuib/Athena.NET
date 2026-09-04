using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 6 hardening (final correctness pass), item 4: MapTcpServer.RunAsync's accept loop and its
// monster-authority loop must supervise EACH OTHER - a deterministic monster-loop failure must make
// RunAsync fault/complete promptly, never sit indefinitely blocked inside AcceptTcpClientAsync
// waiting for some OTHER, unrelated reason to end the accept loop first. This is exercised by
// actually calling MapTcpServer.RunAsync itself (every other orchestration test in this Net/ folder
// only drives ProcessOneMonsterTickAsync directly).
//
// The fault is injected via ValidateMonsterAttackWindowAsync (called by MonsterAttackCadenceExecutor,
// NOT scoped inside ProcessOneMonsterTickAsync's own per-map try/catch - see that method's own doc
// comment: a per-map poll failure is correctly ISOLATED to that one map per item 7, so a
// KeyNotFoundException from PollMonsterFeedAsync itself would only mark one map permanently failed,
// never escape to fault the whole loop). ValidateMonsterAttackWindowAsync only runs when the cadence
// executor finds an Alive, InAttackRange, engaged, attack-due monster - seeded directly into the
// projection/combat-state below so this test does not depend on a real World poll ever succeeding
// first.
//
// A real per-map poll (and the cadence executor's own per-session grouping) only happens for a
// session MapTcpServer's own eligibility filter accepts (IsWorldMapEligible), which in production
// only becomes true after the full client<->MapServer handshake completes. MapTcpServer's own
// `_sessions` dictionary (populated by the private HandleClientAsync) has no production seam for
// inserting an already-constructed session directly, so this test uses the SAME narrow, test-only
// reflection seam this file's own sibling (MapTcpServerMonsterTickHardeningTests.OverridePresenceId)
// already establishes as this project's accepted pattern for exactly this situation.
public sealed class MapTcpServerRunAsyncSupervisionTests
{
    private const string MapId = "izlude";
    private const int PoringMobId = 1002;

    private static MapConfigStore ConfigStore() => new(new MapConfig { BindIp = IPAddress.Loopback, MapPort = 0 }, "unused.conf");

    // A scripted IWorldRuntime whose ValidateMonsterAttackWindowAsync always throws a deterministic
    // KeyNotFoundException (this project's own established example of a non-retryable invariant
    // failure - see MapTcpServer.IsDeterministicInvariantFailure's own doc comment); PollMonsterFeedAsync
    // returns a harmless no-op incremental page so the map itself never becomes permanently failed
    // via the (correctly isolated, per item 7) per-map poll path.
    private sealed class ThrowsFromValidateAttackWindowRuntime : IWorldRuntime
    {
        public Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldMonsterFeedPage(mapId, cursor?.SimulationEpoch ?? WorldSimulationEpoch.NewEpoch(), WorldMonsterFeedStatus.Ready, Snapshot: null, Entries: [], AsOfSequence: (cursor?.Sequence ?? 0) + 1));

        public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken) =>
            throw new KeyNotFoundException("Simulated unknown generated MobId - deterministic invariant failure.");

        public Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceRegistration("test-partition", mapId, WorldPresenceRegistrationStatus.Registered, 1));
        public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceUnregistration("test-partition", mapId, WorldPresenceUnregistrationStatus.Removed, 0));
        public Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMonsterDeathResult> TryMarkMonsterDeadAsync(WorldMonsterLifeReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    // Builds one real, eligible (IsWorldMapEligible == true) MapClientSession via the internal
    // test-facing constructor and drives it through the full handshake, mirroring
    // MapTcpServerMonsterTickHardeningTests.MakeWorldVisibleSessionAsync exactly - then injects it
    // into `server`'s private `_sessions` field via reflection, the narrow test-only seam this file's
    // own doc comment explains. Also seeds an Alive/InAttackRange/engaged/attack-due monster directly
    // into the projection+combat-state so the cadence executor's own ValidateMonsterAttackWindowAsync
    // call is reached on the very first tick, without depending on a real World poll ever succeeding.
    private static async Task<(MapClientSession Session, TcpClient Client)> InjectEligibleSessionWithDueAttackAsync(MapTcpServer server, MapServerWorld world, IWorldRuntime worldRuntime)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        listener.Stop();

        const uint characterId = 1;
        var connector = new CharServerConnector(ConfigStore());
        var state = new CharacterGameplayState(characterId, 1, 0, 1, 1, 0, 0, 40, 10, 40, 10, 0, 0, 9, 9, 9, 9, 9, 9);
        var session = new MapClientSession(
            (int)characterId, serverClient, connector, iroAuthenticated: true,
            mapName: MapId, x: 100, y: 100,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            accountId: characterId, charId: characterId,
            monsterProjections: world.MonsterProjections, combat: world.Combat, combatState: world.CombatState,
            movementPathProvider: world.MovementPathProvider, collisionProvider: world.Collision,
            players: world.Players, playerVisibility: world.PlayerVisibility, visibilityOptions: world.Visibility,
            distributedWorld: worldRuntime);

        var run = session.RunAsync(CancellationToken.None);
        var auth = new MapAuthOkData(characterId, characterId, 1, 2, 0, 0, false, MapId, 100, 100, 0, 0, 1, "Fixture");
        await session.CompleteIroAuthenticationAsync(auth);

        var stream = client.GetStream();
        await ReadExactAsync(stream, 4 + 6 + 6 + 13);
        var skillListHeader = await ReadExactAsync(stream, 4);
        await ReadExactAsync(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExactAsync(stream, 15); // self weapon
        await ReadExactAsync(stream, 6);  // inventory start
        await ReadExactAsync(stream, 4);  // inventory end

        var eligibilityDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < eligibilityDeadline) await Task.Delay(10);
        Assert.True(session.IsWorldMapEligible);

        // Seed an Alive, InAttackRange, engaged, immediately-due monster - the cadence executor's own
        // ProcessAsync (called unconditionally every tick, see MapTcpServer.ProcessOneMonsterTickAsync)
        // will reach ValidateMonsterAttackWindowAsync for it on the very first tick.
        var epoch = WorldSimulationEpoch.NewEpoch();
        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 500;
        var target = new WorldPlayerTargetReference(characterId, session.PresenceId!.Value);
        var instance = new WorldMonsterInstance(
            actorId, incarnation, MapId, PoringMobId, X: 100, Y: 100,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 100, DestinationY: 100,
            WorldMonsterEngagementState.InAttackRange, target);
        var projection = world.MonsterProjections.GetOrCreate(MapId);
        projection.ApplySnapshot([instance], epoch, world.CombatState);
        world.CombatState.ScheduleNextAttack(new MonsterCombatKey(MapId, epoch, actorId, incarnation), DateTimeOffset.UnixEpoch); // Already due.

        var sessionsField = typeof(MapTcpServer).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MapTcpServer._sessions field not found - test seam broken by a rename.");
        var sessions = (ConcurrentDictionary<int, MapClientSession>)sessionsField.GetValue(server)!;
        sessions[1] = session;

        _ = run;
        return (session, client);
    }

    [Fact]
    public async Task DeterministicMonsterLoopFailure_RunAsyncFaultsPromptly_DoesNotSitWaitingForAnotherAccept()
    {
        var combatState = new MonsterCombatStateStore();
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules(), combatState);
        var world = new MapServerWorld(
            WorldMapRegistry.Tutorial,
            [],
            combat,
            EmptyMapCollisionProvider.Instance,
            new UnverifiedGridLineMovementPathProvider(),
            new MonsterFeedProjectionRegistry(),
            combatState);
        var runtime = new ThrowsFromValidateAttackWindowRuntime();
        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, runtime);

        using var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        var boundPortDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (server.BoundPort == 0 && DateTime.UtcNow < boundPortDeadline) await Task.Delay(10);
        Assert.NotEqual(0, server.BoundPort);

        var (session, client) = await InjectEligibleSessionWithDueAttackAsync(server, world, runtime);
        using var disposableClient = client;

        // The deterministic failure must make RunAsync complete (faulted) well within a small bound -
        // proving it does NOT sit indefinitely blocked in AcceptTcpClientAsync waiting for some OTHER
        // reason to end the accept loop. 100ms tick cadence + a generous safety margin.
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(runTask, completed);
        Assert.True(runTask.IsCompleted, "Expected RunAsync to have completed (faulted) promptly after the deterministic monster-loop failure.");
        Assert.True(runTask.IsFaulted, "Expected RunAsync to fault (propagate the deterministic invariant failure), not complete successfully or hang.");

        var thrown = await Assert.ThrowsAsync<KeyNotFoundException>(() => runTask);
        Assert.Contains("deterministic invariant failure", thrown.Message, StringComparison.OrdinalIgnoreCase);

        client.Close();
        await session.DisposeAsync();
        cts.Cancel();
    }
}
