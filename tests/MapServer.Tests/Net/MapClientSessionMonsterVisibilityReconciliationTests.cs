using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 6 hardening (items 3 and 4): MapClientSession's own monster-visibility reconciliation - a
// monster leaving AOI mid-walk, a resync observing a vanished/dead/old-incarnation/new-epoch actor,
// and World's Died feed entry fanning out to EVERY session that still has the actor visible (not
// only the attacker's own session). Exercised directly against a real socket-backed MapClientSession
// (test-facing constructor) using WorldMonsterProjectionTestHelper-shaped WorldMonsterInstance
// values built by hand (a lighter-weight unit-test style, per this task's own preference, rather
// than a full Orleans TestCluster - none of these behaviors depend on real grain semantics).
public sealed class MapClientSessionMonsterVisibilityReconciliationTests
{
    private const string MapId = "int_land03";
    private const int PoringMobId = 1002;
    private const ushort ViewerX = 100;
    private const ushort ViewerY = 100;

    private static WorldMonsterInstance Alive(uint actorId, WorldMonsterIncarnationId incarnation, ushort x, ushort y) =>
        new(actorId, incarnation, MapId, PoringMobId, x, y, WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: x, DestinationY: y, WorldMonsterEngagementState.Unengaged, EngagedTarget: null);

    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask)> SetupViewerAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), iroAuthenticated: true,
            mapName: MapId, x: ViewerX, y: ViewerY);
        var run = session.RunAsync(CancellationToken.None);
        return (client, stream, session, run);
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return buffer;
    }

    // 0x09FF (ZC_NOTIFY_STANDENTRY) is a variable-length packet (its own name suffix) - read the
    // fixed header first, then the rest per its own embedded length field, mirroring this project's
    // established ReadDynamic idiom (see MapClientSessionMonsterCombatTests' own identical helper).
    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private static MonsterCombatState CombatFor(MonsterCombatStateStore store, string mapId, WorldSimulationEpoch epoch, WorldMonsterInstance instance)
    {
        Assert.True(store.TryGet(new MonsterCombatKey(mapId, epoch, instance.ActorId, instance.IncarnationId), out var combat));
        return combat;
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_ActorWalksOutOfAoi_SendsVanish()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var nearby = Alive(actorId, incarnation, x: (ushort)(ViewerX + 1), y: ViewerY);
        combatState.Register(MapId, epoch, actorId, incarnation, maxHp: 55);

        // Discover it first (within AOI) - the standard discovery path.
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(nearby), movementKind: null, CombatFor(combatState, MapId, epoch, nearby), CancellationToken.None);
        var discoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(discoveryPacket));

        // Now report the SAME actor at a position outside this session's own AOI (ordinary
        // incremental movement, e.g. a Moved/CellCrossed feed entry) - must vanish it for this
        // session, not continue projecting movement for an actor the client can no longer see.
        var farAway = nearby with { X = (ushort)(ViewerX + WorldVisibilityOptions.DefaultAreaSize + 5) };
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(farAway), WorldMonsterMovementKind.CellCrossed, CombatFor(combatState, MapId, epoch, farAway), CancellationToken.None);

        var vanishPacket = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(vanishPacket.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonOutOfSight, vanishPacket[6]);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconcileMonsterVisibilityAsync_ActorVanishedFromSnapshot_SendsVanish()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var instance = Alive(actorId, incarnation, x: ViewerX, y: ViewerY);

        var projections = new MonsterFeedProjectionRegistry();
        var projection = projections.GetOrCreate(MapId);
        projection.ApplySnapshot([instance], epoch, combatState);

        // First reconciliation discovers it.
        await session.ReconcileMonsterVisibilityAsync(projection, combatState, CancellationToken.None);
        var discoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(discoveryPacket));

        // A fresh snapshot that no longer contains this ActorId at all (vanished/reaped) - a second
        // reconciliation must vanish it for this session.
        projection.ApplySnapshot([], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, combatState, CancellationToken.None);

        var vanishPacket = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(vanishPacket.AsSpan(2)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconcileMonsterVisibilityAsync_NewIncarnationSameActorId_VanishesOldThenRediscoversNew()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        var oldIncarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var oldInstance = Alive(actorId, oldIncarnation, x: ViewerX, y: ViewerY);

        var projections = new MonsterFeedProjectionRegistry();
        var projection = projections.GetOrCreate(MapId);
        projection.ApplySnapshot([oldInstance], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, combatState, CancellationToken.None);
        await ReadDynamic(stream); // Discovery of the old life.

        // A fresh snapshot with the SAME ActorId but a DIFFERENT (new) IncarnationId - the old life
        // must be vanished first, never silently reused for the new life.
        var newIncarnation = oldIncarnation.Next();
        var newInstance = Alive(actorId, newIncarnation, x: ViewerX, y: ViewerY);
        projection.ApplySnapshot([newInstance], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, combatState, CancellationToken.None);

        var vanishPacket = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(vanishPacket.AsSpan(2)));

        var rediscoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(rediscoveryPacket));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(rediscoveryPacket.AsSpan(5)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconcileMonsterVisibilityAsync_NewEpoch_VanishesEverythingThenRediscovers()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var combatState = new MonsterCombatStateStore();
        var oldEpoch = WorldSimulationEpoch.NewEpoch();
        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var instance = Alive(actorId, incarnation, x: ViewerX, y: ViewerY);

        var projections = new MonsterFeedProjectionRegistry();
        var projection = projections.GetOrCreate(MapId);
        projection.ApplySnapshot([instance], oldEpoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, combatState, CancellationToken.None);
        await ReadDynamic(stream);

        // The map's own SimulationEpoch changed (World simulation rebuilt) - even though the SAME
        // ActorId+IncarnationId+position re-appears, the session's own prior-epoch view of it is
        // stale and must be vanished before being rediscovered under the new epoch.
        var newEpoch = WorldSimulationEpoch.NewEpoch();
        projection.ApplySnapshot([instance], newEpoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, combatState, CancellationToken.None);

        var vanishPacket = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));

        var rediscoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(rediscoveryPacket));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Item 4: World's Died feed entry must be fanned out to EVERY session that still has the actor
    // visible - proven with two sessions, only ONE of which (the "attacker") already had its own
    // visible-tracker entry cleared (simulating its own local confirmed-kill path having already run
    // synchronously before this feed entry is observed) - only the OTHER, still-visible session
    // receives a vanish; the attacker's own session receives no duplicate.
    [Fact]
    public async Task NotifyMonsterDiedAsync_BystanderStillVisible_ReceivesVanish_AttackerAlreadyClearedReceivesNothing()
    {
        var (attackerClient, attackerStream, attackerSession, attackerRun) = await SetupViewerAsync();
        var (bystanderClient, bystanderStream, bystanderSession, bystanderRun) = await SetupViewerAsync();
        using var _a = attackerClient;
        using var _b = bystanderClient;

        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var instance = Alive(actorId, incarnation, x: ViewerX, y: ViewerY);
        combatState.Register(MapId, epoch, actorId, incarnation, maxHp: 55);
        var combat = CombatFor(combatState, MapId, epoch, instance);

        // Both sessions discover the monster first.
        await attackerSession.NotifyMonsterMovedAsync(new WorldMonsterActorView(instance), movementKind: null, combat, CancellationToken.None);
        await ReadDynamic(attackerStream);
        await bystanderSession.NotifyMonsterMovedAsync(new WorldMonsterActorView(instance), movementKind: null, combat, CancellationToken.None);
        await ReadDynamic(bystanderStream);

        // Simulate the attacker's own local confirmed-kill path having ALREADY run synchronously
        // (its own death-vanish already sent via a different, existing code path in production) -
        // here that just means its own visible-tracker no longer has this actor, achieved directly
        // via the existing ForgetPlayer-shaped API this class already exposes for exactly this kind
        // of "this session no longer considers this actor visible" state.
        attackerSession.ForgetPlayer(actorId);

        // Now the World feed's own Died entry is fanned out to both sessions.
        await attackerSession.NotifyMonsterDiedAsync(actorId, CancellationToken.None);
        await bystanderSession.NotifyMonsterDiedAsync(actorId, CancellationToken.None);

        // Bystander receives exactly one vanish, with reason=Died - item 5 of the Step 6
        // correctness-hardening pass: an authoritative World death must use reason=Died for every
        // observer, never reason=OutOfSight (that reason is reserved for AOI exit/resync
        // disappearance/map visibility loss only).
        var bystanderVanish = await ReadExact(bystanderStream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(bystanderVanish));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(bystanderVanish.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, bystanderVanish[6]);

        // Attacker receives NO duplicate vanish - confirmed by a harmless ping round-trip landing
        // next instead of any vanish bytes.
        await attackerStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(attackerStream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        attackerClient.Close();
        bystanderClient.Close();
        await attackerRun.WaitAsync(TimeSpan.FromSeconds(5));
        await bystanderRun.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Item 5's own "no packet needed must not mean skip state cleanup" requirement: a session whose
    // generic _visibleActorIds ALREADY says an actor is invisible (the attacker's own case above)
    // must still have its monster-specific visibility/incarnation state (_monsterVisibility) cleaned
    // up by NotifyMonsterDiedAsync - proven here by reconciling a FRESH incarnation for the SAME
    // ActorId immediately afterward and confirming it is treated as a genuine rediscovery (a stand
    // entry is sent), never silently compared against stale leftover metadata for the OLD life.
    [Fact]
    public async Task NotifyMonsterDiedAsync_AlreadyInvisibleActor_StillCleansMonsterVisibilityState_RespawnIsRediscovered()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        var oldIncarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var instance = Alive(actorId, oldIncarnation, x: ViewerX, y: ViewerY);
        combatState.Register(MapId, epoch, actorId, oldIncarnation, maxHp: 55);
        var combat = CombatFor(combatState, MapId, epoch, instance);

        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(instance), movementKind: null, combat, CancellationToken.None);
        await ReadDynamic(stream);

        // This session's own generic tracker already says the actor is invisible (mirroring the
        // attacker's own already-cleared case) - NotifyMonsterDiedAsync must still run its own
        // monster-visibility cleanup rather than short-circuiting entirely.
        session.ForgetPlayer(actorId);
        await session.NotifyMonsterDiedAsync(actorId, CancellationToken.None);

        // A respawn under a NEW incarnation, same ActorId/position, reconciled via the ordinary full
        // reconciliation path - must be treated as a genuine fresh discovery (a stand entry is sent),
        // proving no stale _monsterVisibility entry for the OLD incarnation survived to interfere.
        var newIncarnation = oldIncarnation.Next();
        var respawned = Alive(actorId, newIncarnation, x: ViewerX, y: ViewerY);
        var projections = new MonsterFeedProjectionRegistry();
        var projection = projections.GetOrCreate(MapId);
        projection.ApplySnapshot([respawned], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, combatState, CancellationToken.None);

        var rediscoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(rediscoveryPacket));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(rediscoveryPacket.AsSpan(5)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
