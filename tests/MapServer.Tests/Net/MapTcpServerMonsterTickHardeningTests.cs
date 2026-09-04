using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 6 hardening (items 6, 7, 9): MapTcpServer's own monster-tick orchestration must never poll
// an unauthenticated/not-world-visible session by map id (item 6), must survive an unexpected
// exception from one map's World RPC without permanently faulting the whole tick loop (item 7),
// and MonsterAttackCadenceExecutor's own local target-session selection must require BOTH
// CharacterId AND PresenceId to match before mutating player HP (item 9). These are exercised with
// small scripted IWorldRuntime fakes (World's own real grain semantics are not under test here -
// see InMemoryTestWorldRuntime's own doc comment for why a scripted fake is the right tool for
// exercising MapServer's own orchestration logic in isolation).
public sealed class MapTcpServerMonsterTickHardeningTests
{
    private static MapConfigStore ConfigStore() => new(new MapConfig(), "unused.conf");

    private static MapServerWorld MakeWorld()
    {
        var combatState = new MonsterCombatStateStore();
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules(), combatState);
        return new MapServerWorld(
            WorldMapRegistry.Tutorial,
            [],
            combat,
            EmptyMapCollisionProvider.Instance,
            new UnverifiedGridLineMovementPathProvider(),
            new MonsterFeedProjectionRegistry(),
            combatState);
    }

    // A connected-but-not-yet-authenticated session (the internal test-facing constructor's default
    // `iroAuthenticated: false`) has an empty CurrentMapName and IsWorldMapEligible == false - see
    // MapClientSession's own doc comment on that property. Passing it into
    // ProcessOneMonsterTickAsync alongside zero real sessions must not throw (WorldMapId.Normalize
    // would reject an empty map id) and must never cause PollMonsterFeedAsync to be called for it.
    [Fact]
    public async Task ProcessOneMonsterTickAsync_UnauthenticatedSession_NeverPolledAndNoException()
    {
        var world = MakeWorld();
        var pollCalls = new List<string>();
        var scripted = new ScriptedWorldRuntime { OnPollMonsterFeed = mapId => pollCalls.Add(mapId) };
        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, scripted);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        listener.Stop();
        using var disposableClient = client;

        var connector = new CharServerConnector(ConfigStore());
        var unauthenticatedSession = new MapClientSession(1, serverClient, connector, iroAuthenticated: false);

        Assert.False(unauthenticatedSession.IsWorldMapEligible);
        Assert.Equal(string.Empty, unauthenticatedSession.CurrentMapName);

        await server.ProcessOneMonsterTickAsync([unauthenticatedSession], CancellationToken.None);

        Assert.Empty(pollCalls);
        Assert.False(world.MonsterProjections.TryGet(string.Empty, out _));

        await unauthenticatedSession.DisposeAsync();
    }

    // Item 7: an unexpected (non-IOException/non-OperationCanceledException) exception thrown from
    // PollMonsterFeedAsync for one map must not fault ProcessOneMonsterTickAsync outright - a
    // SUBSEQUENT call/tick for the SAME (or a different) map must still succeed normally afterward.
    [Fact]
    public async Task ProcessOneMonsterTickAsync_UnexpectedExceptionFromOneMap_DoesNotFaultSubsequentTicks()
    {
        var world = MakeWorld();
        var callCount = 0;
        var scripted = new ScriptedWorldRuntime
        {
            OnPollMonsterFeed = _ =>
            {
                callCount++;
                if (callCount == 1) throw new InvalidOperationException("Simulated transient World RPC failure.");
            },
        };
        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, scripted);

        var (session, client) = await MakeWorldVisibleSessionAsync(world, scripted, mapId: "izlude");
        using var _ = client;

        // First tick: the scripted fake throws for this map - must not propagate out of
        // ProcessOneMonsterTickAsync at all.
        var firstTickException = await Record.ExceptionAsync(() => server.ProcessOneMonsterTickAsync([session], CancellationToken.None));
        Assert.Null(firstTickException);
        Assert.Equal(1, callCount);

        // Second tick against the SAME map must succeed normally - the loop/task is still alive and
        // functional, and the previously-thrown exception did not leave any partial/corrupted state
        // behind that would make a later, successful call fail too.
        var secondTickException = await Record.ExceptionAsync(() => server.ProcessOneMonsterTickAsync([session], CancellationToken.None));
        Assert.Null(secondTickException);
        Assert.Equal(2, callCount);

        await session.DisposeAsync();
    }

    // Item 7's own correction pass: a DETERMINISTIC invariant/configuration failure (e.g. an unknown
    // generated MobId, surfaced as KeyNotFoundException) must NOT be retried forever like an ordinary
    // transient failure - the affected map is put into an explicit permanently-failed state (never
    // polled again this process lifetime), while a DIFFERENT map in the SAME tick still proceeds
    // normally.
    [Fact]
    public async Task ProcessOneMonsterTickAsync_DeterministicInvariantFailureFromOneMap_NeverRetriesThatMap_OtherMapStillProceeds()
    {
        var world = MakeWorld();
        var brokenMapCalls = 0;
        var healthyMapCalls = 0;
        var scripted = new ScriptedWorldRuntime
        {
            OnPollMonsterFeed = mapId =>
            {
                if (string.Equals(mapId, "broken", StringComparison.OrdinalIgnoreCase))
                {
                    brokenMapCalls++;
                    throw new KeyNotFoundException("Simulated unknown generated MobId.");
                }
                healthyMapCalls++;
            },
        };
        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, scripted);

        var (brokenSession, brokenClient) = await MakeWorldVisibleSessionAsync(world, scripted, mapId: "broken", characterId: 101);
        var (healthySession, healthyClient) = await MakeWorldVisibleSessionAsync(world, scripted, mapId: "izlude", characterId: 102);
        using var _b = brokenClient;
        using var _h = healthyClient;

        // First tick: "broken" throws KeyNotFoundException - must not propagate, and "izlude" must
        // still be polled in the SAME tick.
        var firstTickException = await Record.ExceptionAsync(() => server.ProcessOneMonsterTickAsync([brokenSession, healthySession], CancellationToken.None));
        Assert.Null(firstTickException);
        Assert.Equal(1, brokenMapCalls);
        Assert.Equal(1, healthyMapCalls);

        // Second tick: "broken" must NEVER be polled again (permanently failed - the deterministic
        // failure would just reproduce identically), while "izlude" continues normally.
        var secondTickException = await Record.ExceptionAsync(() => server.ProcessOneMonsterTickAsync([brokenSession, healthySession], CancellationToken.None));
        Assert.Null(secondTickException);
        Assert.Equal(1, brokenMapCalls); // Unchanged - never retried.
        Assert.Equal(2, healthyMapCalls); // The healthy map's own second poll.

        await brokenSession.DisposeAsync();
        await healthySession.DisposeAsync();
    }

    // Item 9: MonsterAttackCadenceExecutor's own LOCAL session-selection step must require BOTH
    // CharacterId AND PresenceId to match World's own WorldPlayerTargetReference before ever
    // reaching World's ValidateMonsterAttackWindowAsync recheck - a reconnect race (old session
    // disconnecting, a NEW session with the SAME CharacterId but a DIFFERENT PresenceId already
    // active on this map) must never have player HP mutated onto the mismatched local session. This
    // is proven at the MapServer orchestration level (MonsterAttackCadenceExecutor via
    // MapTcpServer's own tick), independent of World's own already-covered grain-level guard
    // (ReconnectWithSameCharacterId_NewPresenceId_CannotInheritOldMonsterAttackTarget in
    // MapTcpServerMonsterAuthorityIntegrationTests.cs covers THAT).
    [Fact]
    public async Task LocalSessionSelection_PresenceIdMismatch_NeverReachesValidateAttackWindow_NoHpMutation()
    {
        var world = MakeWorld();
        const uint characterId = 42;
        var worldPresenceId = Guid.NewGuid(); // World's own target reference points at THIS presence.
        var localSessionPresenceId = Guid.NewGuid(); // The only local session shares CharacterId but NOT this PresenceId.
        Assert.NotEqual(worldPresenceId, localSessionPresenceId);

        var validateCalls = 0;
        const string mapId = "izlude";
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint monsterActorId = 500;
        var monsterIncarnation = WorldMonsterIncarnationId.First;
        var target = new WorldPlayerTargetReference(characterId, worldPresenceId);
        var monsterInstance = new WorldMonsterInstance(
            monsterActorId, monsterIncarnation, mapId, MobId: 1002, X: 100, Y: 100,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 100, DestinationY: 100,
            WorldMonsterEngagementState.InAttackRange, target);

        // The scripted PollMonsterFeedAsync always reports the SAME fixed epoch/snapshot - a first
        // poll (cursor null) bootstraps it; every later poll (this test drives only one tick, so
        // only the first ever happens here) would otherwise need to stay a no-op incremental page so
        // it never resets what this test seeded - see PollMonsterFeedAsync's own body below.
        var scripted = new ScriptedWorldRuntime
        {
            FixedEpoch = epoch,
            FixedSnapshot = [monsterInstance],
            OnValidateMonsterAttackWindow = _ =>
            {
                validateCalls++;
                return new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.Valid); // Would succeed IF ever reached with the right params.
            },
        };

        var key = new MonsterCombatKey(mapId, epoch, monsterActorId, monsterIncarnation);

        var (localSession, client) = await MakeWorldVisibleSessionAsync(world, scripted, mapId, characterId, localSessionPresenceId);
        using var _ = client;

        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, scripted);
        // First tick: bootstraps the projection/combat-state from the scripted snapshot (this is
        // also the SAME tick MakeWorldVisibleSessionAsync's own EnterPlayerWorldAsync call already
        // required IsWorldMapEligible for, so this reuses the identical epoch/monster/target setup
        // rather than needing a separate priming tick).
        await server.ProcessOneMonsterTickAsync([localSession], CancellationToken.None);
        Assert.True(world.CombatState.TryGet(key, out var combatBefore));

        // Second tick: the monster is now Alive+InAttackRange with a target - this is the tick that
        // actually exercises MonsterAttackCadenceExecutor's own local session-selection guard.
        await server.ProcessOneMonsterTickAsync([localSession], CancellationToken.None);

        // The mismatched local session must never have been selected as the attack target -
        // ValidateMonsterAttackWindowAsync is never even called (session-selection rejects before
        // reaching World), and no HP mutation occurs on the local combat-state entry.
        Assert.Equal(0, validateCalls);
        Assert.True(world.CombatState.TryGet(key, out var combatAfter));
        Assert.Equal(combatBefore.CurrentHp, combatAfter.CurrentHp);

        await localSession.DisposeAsync();
    }

    private static async Task<(MapClientSession Session, TcpClient Client)> MakeWorldVisibleSessionAsync(
        MapServerWorld world, IWorldRuntime worldRuntime, string mapId, uint characterId = 1, Guid? presenceId = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        listener.Stop();

        var connector = new CharServerConnector(ConfigStore());
        var state = new CharacterGameplayState(characterId, 1, 0, 1, 1, 0, 0, 40, 10, 40, 10, 0, 0, 9, 9, 9, 9, 9, 9);
        var session = new MapClientSession(
            (int)characterId, serverClient, connector, iroAuthenticated: true,
            mapName: mapId, x: 100, y: 100,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            accountId: characterId, charId: characterId,
            monsterProjections: world.MonsterProjections, combat: world.Combat, combatState: world.CombatState,
            movementPathProvider: world.MovementPathProvider, collisionProvider: world.Collision,
            players: world.Players, playerVisibility: world.PlayerVisibility, visibilityOptions: world.Visibility,
            distributedWorld: worldRuntime);

        var run = session.RunAsync(CancellationToken.None);
        var auth = new MapAuthOkData(characterId, characterId, 1, 2, 0, 0, false, mapId, 100, 100, 0, 0, 1, "Fixture");
        await session.CompleteIroAuthenticationAsync(auth);

        // Drain the fixed iRO bootstrap burst (0x0B18/0x0283/0x0ADE/0x02EB) plus the variable-length
        // 0x0B32 skill list, then send the client's own 0x007D "map-loaded" packet - the ONLY thing
        // that actually drives EnterPlayerWorldAsync (which sets IsWorldMapEligible via a real
        // RegisterPresenceAsync call to `worldRuntime`), exactly like the production client
        // handshake and like MapTcpServerMonsterAuthorityIntegrationTests' own ConnectSessionAsync.
        var stream = client.GetStream();
        await ReadExactAsync(stream, 4 + 6 + 6 + 13);
        var skillListHeader = await ReadExactAsync(stream, 4);
        await ReadExactAsync(stream, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExactAsync(stream, 15); // 0x01D7 self weapon
        await ReadExactAsync(stream, 6);  // inventory start
        await ReadExactAsync(stream, 4);  // inventory end

        var eligibilityDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < eligibilityDeadline) await Task.Delay(10);

        // Force PresenceId to the caller-requested value (simulating "this local session's real
        // World presence is a DIFFERENT PresenceId than the one World's own target reference still
        // points at" for the reconnect-race scenario) - only meaningful for the mismatch test above;
        // other callers pass null and keep whatever PresenceId EnterPlayerWorldAsync generated.
        if (presenceId is { } requested) OverridePresenceId(session, requested);

        _ = run; // Intentionally not awaited/observed further by these orchestration-focused tests.
        return (session, client);
    }

    // Test-only reflection seam: MonsterAttackCadenceExecutor reads MapClientSession.PresenceId to
    // perform the local CharacterId+PresenceId match (item 9's own fix) - this test needs to force a
    // MISMATCH against whatever value EnterPlayerWorldAsync already generated internally, without
    // adding a public/internal test-only setter to production code for a single test's sake.
    private static void OverridePresenceId(MapClientSession session, Guid presenceId)
    {
        var field = typeof(MapClientSession).GetField("_presenceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("MapClientSession._presenceId field not found - test seam broken by a rename.");
        field.SetValue(session, presenceId);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    // A minimal scripted IWorldRuntime for these orchestration-focused tests - never reimplements
    // real grain semantics (spawn fingerprinting, sequenced feed/cursor/epoch, engagement rules),
    // matching InMemoryTestWorldRuntime's own documented scope boundary. Every RPC not explicitly
    // scripted via one of the `On*` delegates returns a harmless default/throws NotSupportedException.
    private sealed class ScriptedWorldRuntime : IWorldRuntime
    {
        public Action<string>? OnPollMonsterFeed { get; set; }
        public Func<WorldMonsterAttackWindowQuery, WorldMonsterAttackWindowResult>? OnValidateMonsterAttackWindow { get; set; }
        // When set, the FIRST poll for any given cursor==null bootstraps exactly this snapshot under
        // this fixed epoch; every later poll (cursor already non-null) is a no-op incremental page
        // under the SAME epoch - never a second fresh Snapshot, which would otherwise reset whatever
        // combat-state a test seeded from the first bootstrap.
        public WorldSimulationEpoch? FixedEpoch { get; set; }
        public IReadOnlyList<WorldMonsterInstance>? FixedSnapshot { get; set; }

        public Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId, CancellationToken cancellationToken)
        {
            OnPollMonsterFeed?.Invoke(mapId);
            var epoch = FixedEpoch ?? cursor?.SimulationEpoch ?? WorldSimulationEpoch.NewEpoch();
            if (cursor is null && FixedSnapshot is not null)
                return Task.FromResult(new WorldMonsterFeedPage(mapId, epoch, WorldMonsterFeedStatus.Ready, FixedSnapshot, Entries: null, AsOfSequence: 1));
            // A no-op incremental page (Snapshot: null, Entries: empty) - never a fresh Snapshot on a
            // later poll, which would otherwise ApplySnapshot-reset (and wipe) whatever the first
            // bootstrap (or the test itself) already seeded into the projection/combat-state store.
            return Task.FromResult(new WorldMonsterFeedPage(mapId, epoch, WorldMonsterFeedStatus.Ready, Snapshot: null, Entries: [], AsOfSequence: (cursor?.Sequence ?? 0) + 1));
        }

        public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(OnValidateMonsterAttackWindow?.Invoke(query) ?? new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.StaleLifeReference));

        public Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceRegistration("test-partition", mapId, WorldPresenceRegistrationStatus.Registered, 1));
        public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceUnregistration("test-partition", mapId, WorldPresenceUnregistrationStatus.Removed, 0));

        public Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ScriptedWorldRuntime does not script LoadMonsterSpawnsAsync for these tests.");
        public Task<WorldMonsterDeathResult> TryMarkMonsterDeadAsync(WorldMonsterLifeReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ScriptedWorldRuntime does not script TryMarkMonsterDeadAsync for these tests.");
        public Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ScriptedWorldRuntime does not script NotifyMonsterAttackedAsync for these tests.");
        public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ScriptedWorldRuntime does not script UpdatePresenceLifeStateAsync for these tests.");
        public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ScriptedWorldRuntime does not script MovePlayerAsync for these tests.");
        public Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ScriptedWorldRuntime does not script TruncateMovementAsync for these tests.");
        public Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ScriptedWorldRuntime does not script CancelMovementAsync for these tests.");
        public Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ScriptedWorldRuntime does not script AdvanceMovementAsync for these tests.");
        public Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ScriptedWorldRuntime does not script TransferPlayerAsync for these tests.");
    }
}
