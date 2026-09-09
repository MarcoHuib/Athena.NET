using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Live-acceptance identity-bug regression: a real capture (accountId=2000000, charId=1) showed
// EVERY player->monster attack rejected with StaleAttackerPresence. Root cause:
// MapClientSession.CharacterId returned _accountId ("CharacterId IS AccountId" - a false
// assumption that happened to be invisible in every prior test, all of which used identical
// AccountId/CharId values). World registers/resolves player presence by the REAL CharacterId
// (_charId) - WorldPartitionGrain's own TryFind(2000000, ...) could never find a presence
// registered under CharacterId=1, so every attack failed identically to the live capture.
//
// Every test in this file uses RADICALLY DIFFERENT AccountId/CharId values (matching the live
// shape exactly: AccountId=2_000_000, CharId=1) so a regression back to "CharacterId == AccountId"
// fails these tests immediately, rather than silently passing the way it did in every pre-existing
// test file that happened to use identical values for both.
public sealed class MapClientSessionAttackerIdentityTests
{
    private const uint LiveAccountId = 2_000_000;
    private const uint LiveCharId = 1;

    private static MapConfigStore ConfigStore() => new(new MapConfig(), "unused.conf");

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
    {
        public bool TrySelectCell(MobSpawnDefinition spawn, int index, out MobPosition position)
        {
            position = new MobPosition(x, y);
            return true;
        }
    }

    // Deterministic, deliberately weak so a single Knife hit does not one-shot G_PORING's 55 HP.
    private static CharacterGameplayState WeakFreshNovice() => new(
        CharacterId: LiveCharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    private static byte[] AttackPacket(uint targetActorId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzAttackRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), targetActorId);
        packet[6] = 7; // DMG_REPEAT
        packet[7] = 0x7f;
        return packet;
    }

    private static readonly TimeSpan SocketReadTimeout = TimeSpan.FromSeconds(10);

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var cts = new CancellationTokenSource(SocketReadTimeout);
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private WorldSimulationEpoch _lastEpoch;

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target, MonsterCombatStateStore CombatState, FakeCombatWorldRuntime FakeWorld)> SetupAsync(FakeCombatWorldRuntime fakeWorld)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var allocator = new WorldActorIdAllocator();
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
        var questDrops = new QuestDropResolver([]);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        _lastEpoch = epoch;
        var combatState = new MonsterCombatStateStore();
        combatState.Register(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value), target.Spawn.Mob.MaxHp);
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(), combatState);
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances);

        var gameplayPersistence = new FixedGameplayStatePersistence(WeakFreshNovice());
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(ConfigStore()), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: LiveAccountId, charId: LiveCharId, monsterProjections: monsterProjections, combat: combat,
            combatState: combatState, distributedWorld: fakeWorld);
        var run = session.RunAsync(CancellationToken.None);
        // CharacterName must be non-empty for EnterPlayerWorldAsync's own BuildCurrentPresence check
        // to succeed - required so RegisterPresenceAsync (and thus this test's own strict presence
        // validation) actually runs.
        await session.CompleteIroAuthenticationAsync(new(LiveAccountId, LiveCharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0, CharacterName: "TestNovice"));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa }); // Client map-loaded packet - drives EnterPlayerWorldAsync/RegisterPresenceAsync.
        await ReadExact(stream, 15);
        await ReadExact(stream, 6);
        await ReadExact(stream, 4);

        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));
        Assert.Equal(target.ActorId, actorId);

        var eligibilityDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < eligibilityDeadline) await Task.Delay(10);
        Assert.True(session.IsWorldMapEligible, "Expected EnterPlayerWorldAsync to have completed and registered a real World presence.");

        return (client, stream, session, run, target, combatState, fakeWorld);
    }

    private uint CurrentHpOf(MonsterCombatStateStore combatState, MobInstance target) =>
        combatState.TryGet(new MonsterCombatKey(target.Map, _lastEpoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value)), out var state) ? state.CurrentHp : 0u;

    // Test 1: player -> monster engagement identity. Registers a real World presence (via the
    // session's own genuine RegisterPresenceAsync call, driven through EnterPlayerWorldAsync - not
    // fabricated), issues a real attack through MapClientSession's own wire path, and asserts
    // FakeCombatWorldRuntime's STRICT presence validation (which genuinely checks the attack
    // command's AttackerCharacterId/AttackerPresenceId against what was ACTUALLY registered, never
    // a fake that blindly returns Acquired) accepts it - proving WorldMonsterAttackedCommand now
    // carries the real CharacterId (1), not the AccountId (2_000_000).
    [Fact]
    public async Task PlayerToMonsterEngagement_UsesRealCharacterId_NotAccountId_AcceptedByStrictPresenceValidation()
    {
        var fakeWorld = new FakeCombatWorldRuntime { StrictPresenceValidation = true };
        var (client, stream, session, run, target, combatState, _) = await SetupAsync(fakeWorld);
        using var _dispose = client;

        var hpBefore = CurrentHpOf(combatState, target);
        Assert.Equal(LiveCharId, session.CharacterId);
        Assert.NotEqual(LiveAccountId, session.CharacterId);

        await stream.WriteAsync(AttackPacket(target.ActorId));

        // Live-acceptance wire-fidelity fix: pinned unit_attack's own due-now branch (unit.cpp:
        // 2971-2978) sends clif_fixpos (0x0088, the ATTACKER's own current position)
        // unconditionally, before the attack-timer-equivalent execution/World-presence check - the
        // field asserted here is the REAL AccountId (wire identity), distinct from the CharacterId
        // this test's own identity fix is about.
        var fixposPacket = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        Assert.Equal(LiveAccountId, BinaryPrimitives.ReadUInt32LittleEndian(fixposPacket.AsSpan(2)));

        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
        Assert.True(damage > 0);

        var hpInfoPacket = await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        Assert.Equal((short)PacketConstants.ZcHpInfo, BinaryPrimitives.ReadInt16LittleEndian(hpInfoPacket));
        Assert.Equal(hpBefore - damage, BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(6)));

        // The exact command World received must carry the REAL CharacterId, never the AccountId.
        Assert.NotNull(fakeWorld.LastNotifyMonsterAttackedCommand);
        Assert.Equal(LiveCharId, fakeWorld.LastNotifyMonsterAttackedCommand!.AttackerCharacterId);
        Assert.NotEqual(LiveAccountId, fakeWorld.LastNotifyMonsterAttackedCommand.AttackerCharacterId);
        Assert.Equal(session.PresenceId, fakeWorld.LastNotifyMonsterAttackedCommand.AttackerPresenceId);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test 2: monster -> player target resolution. Drives the REAL MonsterAttackCadenceExecutor
    // (via MapTcpServer.ProcessOneMonsterTickAsync) with World's own WorldPlayerTargetReference set
    // to CharacterId=1 (the live shape) - the local session (AccountId=2_000_000, CharId=1) must be
    // selected and take the mob's attack. A second scenario proves a session whose CharId does NOT
    // match (even though its ActorId happens to be similar/identical) is correctly rejected.
    [Fact]
    public async Task MonsterToPlayerTargetResolution_SelectsSessionByRealCharacterId_NotAccountId()
    {
        var world = MakeWorld();
        const string mapId = "izlude";
        var presenceId = Guid.NewGuid();
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint monsterActorId = 900;
        var incarnation = WorldMonsterIncarnationId.First;
        // World's own target reference uses the REAL CharacterId (1) - matching the live shape,
        // never the AccountId (2_000_000).
        var target = new WorldPlayerTargetReference(LiveCharId, presenceId);
        var monsterInstance = new WorldMonsterInstance(
            monsterActorId, incarnation, mapId, MobId: 1002, X: 100, Y: 100,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 100, DestinationY: 100,
            WorldMonsterEngagementState.InAttackRange, target);

        var validateCalls = 0;
        var scripted = new ScriptedWorldRuntime
        {
            FixedEpoch = epoch,
            FixedSnapshot = [monsterInstance],
            OnValidateMonsterAttackWindow = query =>
            {
                validateCalls++;
                Assert.Equal(LiveCharId, query.TargetCharacterId);
                Assert.NotEqual(LiveAccountId, query.TargetCharacterId);
                return new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.Valid);
            },
        };

        var key = new MonsterCombatKey(mapId, epoch, monsterActorId, incarnation);
        var (matchingSession, matchingClient) = await MakeWorldVisibleSessionAsync(world, scripted, mapId, accountId: LiveAccountId, charId: LiveCharId, presenceId: presenceId);
        using var _a = matchingClient;

        Assert.Equal(LiveCharId, matchingSession.CharacterId);
        Assert.Equal(presenceId, matchingSession.PresenceId);
        Assert.True(matchingSession.IsWorldMapEligible);
        Assert.Equal(mapId, matchingSession.CurrentMapName, StringComparer.OrdinalIgnoreCase);

        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, scripted);
        await server.ProcessOneMonsterTickAsync([matchingSession], CancellationToken.None); // Bootstraps the projection/combat-state.
        Assert.True(world.CombatState.TryGet(key, out _));

        await server.ProcessOneMonsterTickAsync([matchingSession], CancellationToken.None); // Actually exercises MonsterAttackCadenceExecutor's own session selection.

        Assert.True(validateCalls >= 1, "Expected the correctly-CharId-matching session to be selected and ValidateMonsterAttackWindowAsync to be reached.");

        await matchingSession.DisposeAsync();

        // Second scenario: a session with the SAME AccountId but a DIFFERENT (wrong) CharId must
        // NOT be selected as the attack target, even though a regression back to "CharacterId ==
        // AccountId" would make this session's own (buggy) CharacterId accessor equal
        // LiveAccountId, not the World target's real CharacterId (1) - proving selection is driven
        // by the real, distinct identity, not by coincidence.
        var world2 = MakeWorld();
        var validateCallsWrongChar = 0;
        var scripted2 = new ScriptedWorldRuntime
        {
            FixedEpoch = epoch,
            FixedSnapshot = [monsterInstance],
            OnValidateMonsterAttackWindow = _ =>
            {
                validateCallsWrongChar++;
                return new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.Valid);
            },
        };
        const uint wrongCharId = 999_999;
        var key2 = new MonsterCombatKey(mapId, epoch, monsterActorId, incarnation);
        var (wrongSession, wrongClient) = await MakeWorldVisibleSessionAsync(world2, scripted2, mapId, accountId: LiveAccountId, charId: wrongCharId, presenceId: presenceId);
        using var _b = wrongClient;

        var server2 = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world2, scripted2);
        await server2.ProcessOneMonsterTickAsync([wrongSession], CancellationToken.None);
        Assert.True(world2.CombatState.TryGet(key2, out var combatBefore));

        await server2.ProcessOneMonsterTickAsync([wrongSession], CancellationToken.None);

        Assert.Equal(0, validateCallsWrongChar);
        Assert.True(world2.CombatState.TryGet(key2, out var combatAfter));
        Assert.Equal(combatBefore.CurrentHp, combatAfter.CurrentHp);

        await wrongSession.DisposeAsync();
    }

    // Test 3: presence life-state update. A real local Alive->Dead transition must push
    // UpdatePresenceLifeStateAsync with the REAL CharacterId (1), never the AccountId (2_000_000).
    [Fact]
    public async Task PresenceLifeStateUpdate_CarriesRealCharacterId_NotAccountId()
    {
        var fakeWorld = new FakeCombatWorldRuntime();
        var (client, stream, session, run, target, combatState, _) = await SetupAsync(fakeWorld);
        using var _dispose = client;

        // Drain the current HP down to exactly the killing blow using repeated real attacks against
        // the monster is unrelated to the PLAYER's own life state - instead, directly drive the
        // player's own Alive->Dead transition the same way the established pending-life-state test
        // file does: apply incoming mob damage via the session's own real internal seam.
        var applied = await session.ApplyIncomingMobBasicAttackAsync(damage: 999, CancellationToken.None);
        Assert.NotNull(applied);
        Assert.Equal(0u, applied!.Value.HpAfter);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (fakeWorld.UpdatePresenceLifeStateCallCount < 1 && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.NotNull(fakeWorld.LastUpdatePresenceLifeStateUpdate);
        Assert.Equal(LiveCharId, fakeWorld.LastUpdatePresenceLifeStateUpdate!.CharacterId);
        Assert.NotEqual(LiveAccountId, fakeWorld.LastUpdatePresenceLifeStateUpdate.CharacterId);
        Assert.Equal(session.PresenceId, fakeWorld.LastUpdatePresenceLifeStateUpdate.PresenceId);
        Assert.False(fakeWorld.LastUpdatePresenceLifeStateUpdate.IsAlive);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test 4: identity regression - explicit, direct assertion that AccountId and CharacterId are
    // genuinely distinct on this session, matching the live capture exactly, so nobody can later
    // collapse the two identities again without this test failing immediately.
    [Fact]
    public async Task AccountIdAndCharacterId_AreDistinctIdentities_MatchingLiveCaptureShape()
    {
        var fakeWorld = new FakeCombatWorldRuntime();
        var (client, _, session, run, _, _, _) = await SetupAsync(fakeWorld);
        using var _dispose = client;

        Assert.Equal(2_000_000u, session.AccountId);
        Assert.Equal(1u, session.CharacterId);
        Assert.NotEqual(session.AccountId, session.CharacterId);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

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

    private static async Task<(MapClientSession Session, TcpClient Client)> MakeWorldVisibleSessionAsync(
        MapServerWorld world, IWorldRuntime worldRuntime, string mapId, uint accountId, uint charId, Guid presenceId)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        listener.Stop();

        var connector = new CharServerConnector(ConfigStore());
        var state = new CharacterGameplayState(charId, 1, 0, 1, 1, 0, 0, 40, 10, 40, 10, 0, 0, 9, 9, 9, 9, 9, 9);
        var session = new MapClientSession(
            (int)accountId, serverClient, connector, iroAuthenticated: true,
            mapName: mapId, x: 100, y: 100,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            accountId: accountId, charId: charId,
            monsterProjections: world.MonsterProjections, combat: world.Combat, combatState: world.CombatState,
            movementPathProvider: world.MovementPathProvider, collisionProvider: world.Collision,
            players: world.Players, playerVisibility: world.PlayerVisibility, visibilityOptions: world.Visibility,
            distributedWorld: worldRuntime);

        var run = session.RunAsync(CancellationToken.None);
        var auth = new MapAuthOkData(accountId, charId, 1, 2, 0, 0, false, mapId, 100, 100, 0, 0, 1, "Fixture");
        await session.CompleteIroAuthenticationAsync(auth);

        var stream = client.GetStream();
        await ReadExact(stream, 4 + 6 + 6 + 13);
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15);
        await ReadExact(stream, 6);
        await ReadExact(stream, 4);

        var eligibilityDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < eligibilityDeadline) await Task.Delay(10);

        OverridePresenceId(session, presenceId);

        _ = run;
        return (session, client);
    }

    // Test-only reflection seam, mirroring MapTcpServerMonsterTickHardeningTests.cs's own
    // established pattern: forces this session's own _presenceId to the caller-requested value so
    // it exactly matches World's own WorldPlayerTargetReference.PresenceId for the test, without
    // adding a public/internal test-only setter to production code for this alone.
    private static void OverridePresenceId(MapClientSession session, Guid presenceId)
    {
        var field = typeof(MapClientSession).GetField("_presenceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("MapClientSession._presenceId field not found - test seam broken by a rename.");
        field.SetValue(session, presenceId);
    }

    private sealed class ScriptedWorldRuntime : IWorldRuntime
    {
        public Action<string>? OnPollMonsterFeed { get; set; }
        public Func<WorldMonsterAttackWindowQuery, WorldMonsterAttackWindowResult>? OnValidateMonsterAttackWindow { get; set; }
        public WorldSimulationEpoch? FixedEpoch { get; set; }
        public IReadOnlyList<WorldMonsterInstance>? FixedSnapshot { get; set; }

        public Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId, CancellationToken cancellationToken)
        {
            OnPollMonsterFeed?.Invoke(mapId);
            var epoch = FixedEpoch ?? cursor?.SimulationEpoch ?? WorldSimulationEpoch.NewEpoch();
            if (cursor is null && FixedSnapshot is not null)
                return Task.FromResult(new WorldMonsterFeedPage(mapId, epoch, WorldMonsterFeedStatus.Ready, FixedSnapshot, Entries: null, AsOfSequence: 1));
            return Task.FromResult(new WorldMonsterFeedPage(mapId, epoch, WorldMonsterFeedStatus.Ready, Snapshot: null, Entries: [], AsOfSequence: (cursor?.Sequence ?? 0) + 1));
        }

        public Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.Loaded, WorldSimulationEpoch.NewEpoch()));

        public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(OnValidateMonsterAttackWindow?.Invoke(query) ?? new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.StaleLifeReference));

        public Task<WorldMonsterDeathResult> TryMarkMonsterDeadAsync(WorldMonsterLifeReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceRegistration("test-partition", mapId, WorldPresenceRegistrationStatus.Registered, 1));
        public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceUnregistration("test-partition", mapId, WorldPresenceUnregistrationStatus.Removed, 0));
        public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
