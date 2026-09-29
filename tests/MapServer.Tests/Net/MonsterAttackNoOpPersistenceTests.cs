using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Regression: a monster attack that does not change the player's state (a miss / zero damage, or a hit
// on an already-dead player) must NOT perform a durable gameplay-state write. Live evidence: every such
// attack still ran one full MapServer -> CharServer -> SQL update (responseWaitMs ~90 ms for a miss,
// version 237 -> 238) awaited serially inside the shared monster tick, stalling the whole map's feed
// projection (sincePrevPollMs 206 ms). A real HP change must still persist exactly once.
//
// These tests count persistence calls with a spy (never elapsed time) and drive the real production
// paths: CharacterGameplayStateSession, MapClientSession.ApplyIncomingMobBasicAttackAsync,
// NotifyMonsterAttackOutcomeAsync and the real MapTcpServer monster tick.
public sealed class MonsterAttackNoOpPersistenceTests
{
    private const uint AccountId = 2_000_000;
    private const uint CharId = 1;
    private const string MapId = "izlude";

    private static CharacterGameplayState Fresh() =>
        new(CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1, BaseExperience: 0, JobExperience: 0,
            CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10, StatPoints: 0, SkillPoints: 0,
            Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    // Behaves like CharServer's optimistic-concurrency update: every accepted write bumps the row version.
    private sealed class VersionBumpingPersistence(CharacterGameplayState initial) : ICharacterGameplayStatePersistence
    {
        public int Updates;
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(initial);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Updates);
            return Task.FromResult<CharacterGameplayState?>(updated with { Version = expected.Version + 1 });
        }
    }

    private static MapConfigStore ConfigStore() => new(new MapConfig(), "unused.conf");

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

    private static async Task AssertNothingMoreSentAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));
    }

    private static MapServerWorld MakeWorld() => new(
        WorldMapRegistry.Tutorial, [], new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules()),
        EmptyMapCollisionProvider.Instance, new UnverifiedGridLineMovementPathProvider(), new MonsterFeedProjectionRegistry(), new MonsterAttackCadenceStore());

    // Boots a real authenticated, World-visible session on `MapId` at (100,100) backed by the spy.
    private static async Task<(MapClientSession Session, TcpClient Client, NetworkStream Stream, VersionBumpingPersistence Persistence, MapServerWorld World)> CreateSessionAsync(
        IWorldRuntime worldRuntime, Guid presenceId)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        listener.Stop();

        var world = MakeWorld();
        var persistence = new VersionBumpingPersistence(Fresh());
        var session = new MapClientSession(
            (int)AccountId, serverClient, new CharServerConnector(ConfigStore()), iroAuthenticated: true,
            mapName: MapId, x: 100, y: 100,
            gameplayStatePersistence: persistence,
            accountId: AccountId, charId: CharId,
            monsterProjections: world.MonsterProjections, combat: world.Combat, combatState: world.CombatState,
            movementPathProvider: world.MovementPathProvider, collisionProvider: world.Collision,
            players: world.Players, playerVisibility: world.PlayerVisibility, visibilityOptions: world.Visibility,
            distributedWorld: worldRuntime);
        _ = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new MapAuthOkData(AccountId, CharId, 1, 2, 0, 0, false, MapId, 100, 100, 0, 0, 1, "Fixture"));

        var stream = client.GetStream();
        await ReadExact(stream, 4 + 6 + 6 + 13);
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15);
        await ReadExact(stream, 6);
        await ReadExact(stream, 4);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(session.IsWorldMapEligible);

        var field = typeof(MapClientSession).GetField("_presenceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(session, presenceId);
        return (session, client, stream, persistence, world);
    }

    // ---- Session level -------------------------------------------------------------------------

    [Fact]
    public async Task Session_MutateIfChanged_UnchangedCandidate_DoesNotPersist_DoesNotBumpVersion()
    {
        var persistence = new VersionBumpingPersistence(Fresh());
        var session = new CharacterGameplayStateSession(AccountId, Fresh(), persistence);

        var result = await session.MutateIfChangedAsync(current => current, CancellationToken.None);

        Assert.Equal(0, persistence.Updates);
        Assert.NotNull(result);
        Assert.Equal(Fresh(), result);
        Assert.Equal(Fresh(), session.State);
        Assert.Equal(1UL, session.State.Version);
    }

    [Fact]
    public async Task Session_MutateIfChanged_ChangedCandidate_PersistsExactlyOnce_AndBumpsVersion()
    {
        var persistence = new VersionBumpingPersistence(Fresh());
        var session = new CharacterGameplayStateSession(AccountId, Fresh(), persistence);

        var result = await session.MutateIfChangedAsync(current => current with { CurrentHp = 33 }, CancellationToken.None);

        Assert.Equal(1, persistence.Updates);
        Assert.NotNull(result);
        Assert.Equal(33u, session.State.CurrentHp);
        Assert.Equal(2UL, session.State.Version);
    }

    [Fact]
    public async Task Session_MutateAsync_UnchangedCandidate_StillPersists_ContractUnchanged()
    {
        // The plain MutateAsync contract is deliberately unchanged (persist what the caller computed,
        // with the persisted-row compare-and-swap); only the opt-in variant skips no-ops.
        var persistence = new VersionBumpingPersistence(Fresh());
        var session = new CharacterGameplayStateSession(AccountId, Fresh(), persistence);

        await session.MutateAsync(current => current, CancellationToken.None);

        Assert.Equal(1, persistence.Updates);
        Assert.Equal(2UL, session.State.Version);
    }

    // ---- MapClientSession.ApplyIncomingMobBasicAttackAsync -------------------------------------

    [Fact]
    public async Task MobAttack_ZeroDamage_ReportsUnchangedHp_WithoutPersistence_OrVersionBump()
    {
        var world = new FakeCombatWorldRuntime();
        var (session, client, _, persistence, _) = await CreateSessionAsync(world, Guid.NewGuid());
        using var _dispose = client;

        var result = await session.ApplyIncomingMobBasicAttackAsync(damage: 0, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal((40u, false), result!.Value);
        Assert.Equal(0, persistence.Updates);
        Assert.Equal(1UL, session.GameplayState!.State.Version);
        Assert.Equal(40u, session.GameplayState.State.CurrentHp);
        Assert.Equal(0, world.UpdatePresenceLifeStateCallCount);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task MobAttack_RealDamage_PersistsExactlyOnce_UpdatesHpAndVersion()
    {
        var world = new FakeCombatWorldRuntime();
        var (session, client, _, persistence, _) = await CreateSessionAsync(world, Guid.NewGuid());
        using var _dispose = client;

        var result = await session.ApplyIncomingMobBasicAttackAsync(damage: 7, CancellationToken.None);

        Assert.Equal((33u, true), result!.Value);
        Assert.Equal(1, persistence.Updates);
        Assert.Equal(33u, session.GameplayState!.State.CurrentHp);
        Assert.Equal(2UL, session.GameplayState.State.Version);
        Assert.Equal(0, world.UpdatePresenceLifeStateCallCount); // Not a death.
        await session.DisposeAsync();
    }

    [Fact]
    public async Task MobAttack_LethalDamage_PersistsOnce_ReportsDeath_ReconcilesWorldLifeState_ThenLaterHitsDoNotPersist()
    {
        var world = new FakeCombatWorldRuntime();
        var presenceId = Guid.NewGuid();
        var (session, client, _, persistence, _) = await CreateSessionAsync(world, presenceId);
        using var _dispose = client;

        var lethal = await session.ApplyIncomingMobBasicAttackAsync(damage: 999, CancellationToken.None);

        Assert.Equal((0u, true), lethal!.Value);
        Assert.Equal(1, persistence.Updates);
        Assert.Equal(0u, session.GameplayState!.State.CurrentHp);
        Assert.Equal(2UL, session.GameplayState.State.Version);
        Assert.Equal(1, world.UpdatePresenceLifeStateCallCount);
        Assert.NotNull(world.LastUpdatePresenceLifeStateUpdate);
        Assert.Equal(CharId, world.LastUpdatePresenceLifeStateUpdate!.CharacterId);
        Assert.False(world.LastUpdatePresenceLifeStateUpdate.IsAlive);

        // A further hit on the now-dead player is a no-op: still a successful (non-null) outcome with
        // HpChanged=false, but no second durable write, no version bump and no second World life-state RPC.
        var again = await session.ApplyIncomingMobBasicAttackAsync(damage: 999, CancellationToken.None);

        Assert.Equal((0u, false), again!.Value);
        Assert.Equal(1, persistence.Updates);
        Assert.Equal(2UL, session.GameplayState.State.Version);
        Assert.Equal(1, world.UpdatePresenceLifeStateCallCount);
        await session.DisposeAsync();
    }

    // ---- Wire projection of the outcome (NotifyMonsterAttackOutcomeAsync) ----------------------

    private static async Task<uint> DiscoverMobAsync(MapClientSession session, NetworkStream stream, uint mobActorId)
    {
        var monster = new WorldMonsterInstance(
            mobActorId, WorldMonsterIncarnationId.First, MapId, MobId: 1002, X: 101, Y: 100,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 101, DestinationY: 100,
            WorldMonsterEngagementState.Unengaged, EngagedTarget: null, CurrentHp: 55, MaxHp: 55);
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(monster), movementKind: null, monster, CancellationToken.None);
        var discovery = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(discovery));
        return mobActorId;
    }

    [Fact]
    public async Task MobAttackOutcome_Miss_SendsCombatActionOnly_NoHpParameterPacket()
    {
        var world = new FakeCombatWorldRuntime();
        var (session, client, stream, persistence, _) = await CreateSessionAsync(world, Guid.NewGuid());
        using var _dispose = client;
        var mob = await DiscoverMobAsync(session, stream, 4242);

        var applied = await session.ApplyIncomingMobBasicAttackAsync(damage: 0, CancellationToken.None);
        var outcome = new MonsterAttackActionOutcome(mob, MapId, 101, 100, session.AccountId, Damage: 0, IsMiss: true, SrcSpeed: 672, DstSpeed: 480, applied!.Value.HpAfter, applied.Value.HpChanged);
        await session.NotifyMonsterAttackOutcomeAsync(outcome, CancellationToken.None);

        var action = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(action));
        Assert.Equal(mob, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(2)));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(6)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(22)));
        await AssertNothingMoreSentAsync(stream); // No 0x00B0 SP_HP.
        Assert.Equal(0, persistence.Updates);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task MobAttackOutcome_RealDamage_SendsCombatActionThenHpParameter()
    {
        var world = new FakeCombatWorldRuntime();
        var (session, client, stream, persistence, _) = await CreateSessionAsync(world, Guid.NewGuid());
        using var _dispose = client;
        var mob = await DiscoverMobAsync(session, stream, 4243);

        var applied = await session.ApplyIncomingMobBasicAttackAsync(damage: 7, CancellationToken.None);
        var outcome = new MonsterAttackActionOutcome(mob, MapId, 101, 100, session.AccountId, Damage: 7, IsMiss: false, SrcSpeed: 672, DstSpeed: 480, applied!.Value.HpAfter, applied.Value.HpChanged);
        await session.NotifyMonsterAttackOutcomeAsync(outcome, CancellationToken.None);

        var action = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(action));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(22)));
        var hp = await ReadExact(stream, 8);
        Assert.Equal((short)0x00B0, BinaryPrimitives.ReadInt16LittleEndian(hp));
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(hp.AsSpan(2))); // SP_HP.
        Assert.Equal(33u, BinaryPrimitives.ReadUInt32LittleEndian(hp.AsSpan(4)));
        Assert.Equal(1, persistence.Updates);
        await session.DisposeAsync();
    }

    // ---- Full monster tick (World feed -> cadence executor -> outcome fan-out) -----------------

    private sealed class ScriptedRuntime : IWorldRuntime
    {
        public WorldSimulationEpoch? FixedEpoch { get; set; }
        public IReadOnlyList<WorldMonsterInstance>? FixedSnapshot { get; set; }

        public Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId, CancellationToken cancellationToken)
        {
            var epoch = FixedEpoch ?? cursor?.SimulationEpoch ?? WorldSimulationEpoch.NewEpoch();
            if (cursor is null && FixedSnapshot is not null)
                return Task.FromResult(new WorldMonsterFeedPage(mapId, epoch, WorldMonsterFeedStatus.Ready, FixedSnapshot, Entries: null, AsOfSequence: 1));
            return Task.FromResult(new WorldMonsterFeedPage(mapId, epoch, WorldMonsterFeedStatus.Ready, Snapshot: null, Entries: [], AsOfSequence: (cursor?.Sequence ?? 0) + 1));
        }

        public Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.Loaded, WorldSimulationEpoch.NewEpoch()));
        public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.Valid));
        public Task<WorldMonsterDamageResult> ApplyMonsterDamageAsync(WorldMonsterDamageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken) => throw new NotSupportedException();
        // Item 14: a semantically-correct EMPTY feed - this fake registers no player-feed state of
        // its own, so an always-empty Ready bootstrap is the correct (not merely convenient) answer:
        // there really are zero players for MapTcpServer's per-map tick loop to discover through
        // this fake, matching how a genuinely empty, loaded map's real feed page would look.
        public Task<WorldPlayerFeedPage> PollPlayerFeedAsync(WorldPlayerFeedCursor? cursor, string mapId, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPlayerFeedPage(mapId, WorldSimulationEpoch.NewEpoch(), WorldPlayerFeedStatus.Ready, [], Entries: null, AsOfSequence: 0));
        public Task<WorldPlayerLookUpdateResult> UpdatePlayerLookAsync(string mapId, uint characterId, Guid presenceId, byte direction, byte headDirection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldPlayerPublicStateUpdateResult> UpdatePlayerPublicStateAsync(string mapId, uint characterId, Guid presenceId, WorldPlayerPublicState publicState, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldMovementProjectionResult> ConfirmMovementProjectionAsync(WorldMovementProjectionConfirmation confirmation, CancellationToken cancellationToken) => throw new NotSupportedException();
        // Item 14: a real no-op success - this fake genuinely does resolve a local monster attack
        // for the tests that use it, so MonsterAttackCadenceExecutor.TryApplyAttackAsync's own
        // unconditional publish-after-success call must not fault; the cross-replica projection
        // itself is not under test here.
        public Task<WorldMonsterAttackPublishResult> PublishMonsterAttackActionAsync(WorldMonsterAttackActionCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldMonsterAttackPublishResult(WorldMonsterAttackPublishStatus.Published));
        public Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, WorldPlayerPublicState publicState, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceRegistration("test-partition", mapId, WorldPresenceRegistrationStatus.Registered, 1));
        public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceUnregistration("test-partition", mapId, WorldPresenceUnregistrationStatus.Removed, 0));
        public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task MonsterTick_MissedAttack_SendsActionAndAdvancesCadence_WithoutAnyGameplayStatePersistence()
    {
        var presenceId = Guid.NewGuid();
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint mobActorId = 901;
        var incarnation = WorldMonsterIncarnationId.First;
        // Poring (generated Attack: 1) against this player's DEF always resolves to a miss/0 damage.
        var monster = new WorldMonsterInstance(
            mobActorId, incarnation, MapId, MobId: 1002, X: 100, Y: 100,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 100, DestinationY: 100,
            WorldMonsterEngagementState.InAttackRange, new WorldPlayerTargetReference(CharId, presenceId), CurrentHp: 55, MaxHp: 55);
        var scripted = new ScriptedRuntime { FixedEpoch = epoch, FixedSnapshot = [monster] };
        var (session, client, stream, persistence, world) = await CreateSessionAsync(scripted, presenceId);
        using var _dispose = client;
        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, scripted);

        // The bootstrap poll discovers the monster and registers its cadence entry; the cadence executor
        // (later in the SAME tick) then performs the first attack.
        await server.ProcessOneMonsterTickAsync([session], CancellationToken.None);

        var discovery = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(discovery));
        var action = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(action));
        Assert.Equal(mobActorId, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(2)));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(6)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(22))); // A miss: the client still sees the swing.
        await AssertNothingMoreSentAsync(stream); // No 0x00B0 HP packet for an unchanged HP.

        Assert.Equal(0, persistence.Updates);
        Assert.Equal(1UL, session.GameplayState!.State.Version);
        Assert.Equal(40u, session.GameplayState.State.CurrentHp);

        // The cadence slot WAS consumed by the accepted (missed) attack.
        var key = new MonsterCombatKey(MapId, epoch, mobActorId, incarnation);
        Assert.True(world.CombatState.TryGet(key, out var combat));
        Assert.NotNull(combat.NextAttackAt);
        await session.DisposeAsync();
    }
}
