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

// Step 7 substep 7 (§14.5): MapClientSession.NotifyMonsterRespawnedAsync is the real Respawned hook
// LethalDeathProjectionArbiter's own ForgetProjectedForActor doc comment anticipated - these tests
// prove the WIRING (session -> its own arbiter's ForgetProjectedForActor with the correct identity),
// complementing LethalDeathProjectionArbiterTests.cs's pure in-memory coverage of
// ForgetProjectedForActor's own removal semantics. Driven against a real socket-backed
// MapClientSession (this file's established convention, matching
// MapClientSessionLethalDeathProjectionRaceTests.cs) so a genuine successful lethal kill produces a
// REAL `_alreadyProjected` marker via the session's own live attack path, never a hand-constructed one.
public sealed class MapClientSessionRespawnCleanupTests
{
    private const uint AccountId = 71;
    private const uint CharId = 73;

    private static byte[] AttackPacket(uint targetActorId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzAttackRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), targetActorId);
        packet[6] = 7; // DMG_REPEAT
        packet[7] = 0x7f;
        return packet;
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private static CharacterGameplayState StrongAttacker() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 99, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 100, CurrentSp: 10, MaxHp: 100, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 99, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 99, Luck: 9);

    private sealed class RecordingGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
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

    private sealed record Scenario(
        TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask,
        string MapId, WorldSimulationEpoch Epoch, uint ActorId, WorldMonsterIncarnationId Incarnation);

    // Sets up a session and drives ONE genuine, successful (non-raced) lethal kill through the real
    // live attack path - MaxHp=1 guarantees the very first hit is lethal. This produces a REAL
    // `_alreadyProjected` marker for `life` inside the session's own LethalDeathProjectionArbiter via
    // the actual CompleteInFlight(markProjected: true) call site, never a hand-constructed one.
    private static async Task<Scenario> SetupAfterGenuineKillAsync()
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
        var combatState = new MonsterCombatStateStore();
        var incarnation = new WorldMonsterIncarnationId(target.IncarnationId.Value);
        combatState.Register(target.Map, epoch, target.ActorId, incarnation, maxHp: 1);
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(), combatState);
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances);

        var gameplayPersistence = new RecordingGameplayStatePersistence(StrongAttacker());
        var fakeWorld = new FakeCombatWorldRuntime();
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsterProjections: monsterProjections, combat: combat,
            combatState: combatState, distributedWorld: fakeWorld);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15);
        await ReadExact(stream, 6);
        await ReadExact(stream, 4);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        await stream.WriteAsync(AttackPacket(actorId));

        // Ordinary, unraced lethal sequence: 0x0088 fixpos, 0x08C8 damage, 0x0977 hp=0, 0x0080 died -
        // draining all four confirms this session genuinely completed its OWN direct lethal
        // projection (the CompleteInFlight(markProjected: true) call site), not merely started one.
        await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        var vanishPacket = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanishPacket[6]);

        return new Scenario(client, stream, session, run, target.Map, epoch, actorId, incarnation);
    }

    // Re-marks `actorId` visible again for THIS session under the SAME exact (old) life identity -
    // simulating "this session can still see this ActorId slot" so a later NotifyMonsterDiedAsync
    // call for the SAME life is not trivially masked by VisibleActorTracker's own independent
    // already-invisible short-circuit (SendMonsterVanishAsync's `TryMarkNotVisible` returning false
    // for an already-invisible actor produces "no packet sent" REGARDLESS of arbiter state, which
    // would make a naive re-test of the same session inconclusive about the arbiter specifically).
    // This is a synthetic discovery (a real dead monster can never be "rediscovered" under its own
    // OLD incarnation) used ONLY to isolate the arbiter's own suppression behavior from this
    // orthogonal visibility-tracker concern - never a scenario production code produces on its own.
    private static Task ReestablishVisibilityForOldLifeAsync(MapClientSession session, string mapId, uint actorId, WorldMonsterIncarnationId incarnation, CancellationToken cancellationToken)
    {
        var rediscovered = new WorldMonsterInstance(
            actorId, incarnation, mapId, GeneratedMobs.GPoring.Id, X: 75, Y: 51,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 75, DestinationY: 51,
            WorldMonsterEngagementState.Unengaged, EngagedTarget: null, CurrentHp: 55, MaxHp: 55);
        return session.NotifyMonsterMovedAsync(new WorldMonsterActorView(rediscovered), movementKind: null, rediscovered, cancellationToken);
    }

    // Test 1: NotifyMonsterRespawnedAsync clears the stale `_alreadyProjected` marker for the OLD
    // incarnation - after calling it with the NEW incarnation, a late Died for the OLD life is no
    // longer suppressed (an ordinary vanish is sent).
    [Fact]
    public async Task NotifyMonsterRespawnedAsync_ClearsStaleAlreadyProjectedMarker_LateDiedForOldLifeNoLongerSuppressed()
    {
        var scenario = await SetupAfterGenuineKillAsync();
        using var _ = scenario.Client;

        var oldLife = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        var newIncarnation = scenario.Incarnation.Next();
        var newLife = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, newIncarnation);

        scenario.Session.NotifyMonsterRespawnedAsync(newLife);

        await ReestablishVisibilityForOldLifeAsync(scenario.Session, scenario.MapId, scenario.ActorId, scenario.Incarnation, CancellationToken.None);
        await ReadDynamic(scenario.Stream); // The synthetic rediscovery's own stand-entry packet.

        await scenario.Session.NotifyMonsterDiedAsync(oldLife, CancellationToken.None);

        var vanishPacket = await ReadExact(scenario.Stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanishPacket[6]);

        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test 2: a session with NO prior projected marker for this ActorId at all (never killed
    // anything here) is a harmless no-op - calling NotifyMonsterRespawnedAsync must not throw and
    // must not disturb ordinary Died delivery for a completely unrelated life.
    [Fact]
    public async Task NotifyMonsterRespawnedAsync_NoPriorProjectedMarker_IsHarmlessNoOp()
    {
        var scenario = await SetupAfterGenuineKillAsync();
        using var _ = scenario.Client;

        // A respawn notification for a COMPLETELY different ActorId this session never interacted
        // with - must not throw, must not affect this session's own state for the actor it DID kill.
        var unrelatedLife = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, ActorId: 999, WorldMonsterIncarnationId.First);
        scenario.Session.NotifyMonsterRespawnedAsync(unrelatedLife);

        // Ordinary bystander Died for a life this session never touched is delivered immediately,
        // unaffected by the no-op call above - proven via the harmless-ping-round-trip idiom for the
        // NEGATIVE case (a bystander session with no visibility for this actor sends nothing anyway,
        // so this specifically proves no exception/corruption occurred, not a suppression property).
        await scenario.Session.NotifyMonsterDiedAsync(unrelatedLife, CancellationToken.None);
        await scenario.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(scenario.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test 3: Respawned still performs ordinary ActorId discovery after the cleanup call - proven at
    // the MapTcpServer.FanOutEntryAsync level (the actual production call site), confirming the
    // cleanup call does not consume/short-circuit the entry, and the existing discovery projection
    // (movementKind: null -> NotifyMonsterMovedAsync) still runs for the SAME entry afterward.
    [Fact]
    public async Task NotifyMonsterRespawnedAsync_DoesNotPreventOrdinaryDiscovery_SessionStillDiscoversNewIncarnation()
    {
        var scenario = await SetupAfterGenuineKillAsync();
        using var _ = scenario.Client;

        var newIncarnation = scenario.Incarnation.Next();
        var newLife = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, newIncarnation);

        // Exactly the two calls FanOutEntryAsync's own Respawned branch performs, in the SAME order:
        // cleanup first, ordinary discovery second - proving the cleanup call itself does not
        // prevent, consume, or otherwise interfere with the discovery call that follows it.
        scenario.Session.NotifyMonsterRespawnedAsync(newLife);

        var respawnedInstance = new WorldMonsterInstance(
            scenario.ActorId, newIncarnation, scenario.MapId, GeneratedMobs.GPoring.Id, X: 75, Y: 51,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 75, DestinationY: 51,
            WorldMonsterEngagementState.Unengaged, EngagedTarget: null, CurrentHp: 55, MaxHp: 55);
        await scenario.Session.NotifyMonsterMovedAsync(new WorldMonsterActorView(respawnedInstance), movementKind: null, respawnedInstance, CancellationToken.None);

        var standPacket = await ReadDynamic(scenario.Stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(standPacket));
        Assert.Equal(scenario.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(standPacket.AsSpan(5)));

        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test 4: multiple sessions each receive the respawn lifecycle cleanup/discovery correctly and
    // independently - each session owns its OWN LethalDeathProjectionArbiter, so session A's own kill
    // and respawn cleanup must never affect session B's independent state, and B must still discover
    // the new incarnation normally.
    [Fact]
    public async Task NotifyMonsterRespawnedAsync_MultipleSessions_EachCleansUpAndDiscoversIndependently()
    {
        var killerScenario = await SetupAfterGenuineKillAsync();
        using var _a = killerScenario.Client;

        // A second, independent session (bystander) on the same conceptual map/life, set up fresh -
        // never attacked anything, has no prior arbiter state of its own at all.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var bystanderClient = new TcpClient();
        var connect = bystanderClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var bystanderServerClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        using var _b = bystanderClient;
        var bystanderStream = bystanderClient.GetStream();
        var bystanderSession = new MapClientSession(
            2, bystanderServerClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), iroAuthenticated: true,
            mapName: killerScenario.MapId, x: 75, y: 51);
        var bystanderRun = bystanderSession.RunAsync(CancellationToken.None);

        var oldLife = new WorldMonsterLifeReference(killerScenario.MapId, killerScenario.Epoch, killerScenario.ActorId, killerScenario.Incarnation);
        var newIncarnation = killerScenario.Incarnation.Next();
        var newLife = new WorldMonsterLifeReference(killerScenario.MapId, killerScenario.Epoch, killerScenario.ActorId, newIncarnation);

        // Both sessions receive the SAME Respawned cleanup call - exactly as FanOutEntryAsync's own
        // per-session loop does.
        killerScenario.Session.NotifyMonsterRespawnedAsync(newLife);
        bystanderSession.NotifyMonsterRespawnedAsync(newLife);

        // The killer session's own stale marker for the OLD life is now cleared (a late Died is no
        // longer suppressed) - proven the same way as test 1.
        await ReestablishVisibilityForOldLifeAsync(killerScenario.Session, killerScenario.MapId, killerScenario.ActorId, killerScenario.Incarnation, CancellationToken.None);
        await ReadDynamic(killerScenario.Stream);
        await killerScenario.Session.NotifyMonsterDiedAsync(oldLife, CancellationToken.None);
        var killerVanish = await ReadExact(killerScenario.Stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(killerVanish));

        // The bystander session, which never had any projected marker at all, still discovers the
        // NEW incarnation normally after its own (no-op) cleanup call.
        var respawnedInstance = new WorldMonsterInstance(
            killerScenario.ActorId, newIncarnation, killerScenario.MapId, GeneratedMobs.GPoring.Id, X: 75, Y: 51,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 75, DestinationY: 51,
            WorldMonsterEngagementState.Unengaged, EngagedTarget: null, CurrentHp: 55, MaxHp: 55);
        await bystanderSession.NotifyMonsterMovedAsync(new WorldMonsterActorView(respawnedInstance), movementKind: null, respawnedInstance, CancellationToken.None);
        var bystanderStand = await ReadDynamic(bystanderStream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(bystanderStand));

        killerScenario.Client.Close();
        bystanderClient.Close();
        await killerScenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
        await bystanderRun.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test 5: unrelated actor/map/epoch projected entries are unaffected by a respawn cleanup call -
    // proven directly against LethalDeathProjectionArbiter (this specific removal-scope guarantee is
    // ForgetProjectedForActor's own responsibility, already exhaustively covered by
    // LethalDeathProjectionArbiterTests.ForgetProjectedForActor_RemovesStaleIncarnations_PreservesExceptedAndUnrelatedEntries
    // and its Multi-respawn/isolation siblings) - this test exists at the SESSION level specifically
    // to confirm NotifyMonsterRespawnedAsync forwards the correct MapId/Epoch/ActorId identity from
    // the WorldMonsterLifeReference it is given, rather than (for example) accidentally sweeping by
    // ActorId alone across maps/epochs.
    [Fact]
    public async Task NotifyMonsterRespawnedAsync_UnrelatedMapOrEpoch_DoesNotClearThatActorsProjectedMarker()
    {
        var scenario = await SetupAfterGenuineKillAsync();
        using var _ = scenario.Client;

        var oldLife = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        var newIncarnation = scenario.Incarnation.Next();

        // A respawn notification for the SAME ActorId but a DIFFERENT map, and separately a
        // DIFFERENT epoch on the SAME map - neither must clear the real projected marker for
        // `oldLife` on scenario.MapId/scenario.Epoch.
        var respawnOnDifferentMap = new WorldMonsterLifeReference("int_land04", scenario.Epoch, scenario.ActorId, newIncarnation);
        var respawnUnderDifferentEpoch = new WorldMonsterLifeReference(scenario.MapId, WorldSimulationEpoch.NewEpoch(), scenario.ActorId, newIncarnation);
        scenario.Session.NotifyMonsterRespawnedAsync(respawnOnDifferentMap);
        scenario.Session.NotifyMonsterRespawnedAsync(respawnUnderDifferentEpoch);

        await ReestablishVisibilityForOldLifeAsync(scenario.Session, scenario.MapId, scenario.ActorId, scenario.Incarnation, CancellationToken.None);
        await ReadDynamic(scenario.Stream);

        // The real marker for oldLife (scenario.MapId/scenario.Epoch) must STILL be intact - a late
        // Died for it must still be suppressed, since neither unrelated respawn call above was a
        // structural match for it.
        await scenario.Session.NotifyMonsterDiedAsync(oldLife, CancellationToken.None);
        await scenario.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(scenario.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
