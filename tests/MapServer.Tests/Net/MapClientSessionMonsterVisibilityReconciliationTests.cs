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
        new(actorId, incarnation, MapId, PoringMobId, x, y, WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: x, DestinationY: y, WorldMonsterEngagementState.Unengaged, EngagedTarget: null, CurrentHp: 55, MaxHp: 55);

    private static WorldMonsterInstance AliveWithHp(uint actorId, WorldMonsterIncarnationId incarnation, ushort x, ushort y, uint currentHp, uint maxHp, bool isWalking = false, ushort destinationX = 0, ushort destinationY = 0) =>
        new(actorId, incarnation, MapId, PoringMobId, x, y, WorldMonsterLifecycleState.Alive, isWalking, DestinationX: isWalking ? destinationX : x, DestinationY: isWalking ? destinationY : y, WorldMonsterEngagementState.Unengaged, EngagedTarget: null, CurrentHp: currentHp, MaxHp: maxHp);

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

    [Fact]
    public async Task NotifyMonsterMovedAsync_ActorWalksOutOfAoi_SendsVanish()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var nearby = Alive(actorId, incarnation, x: (ushort)(ViewerX + 1), y: ViewerY);

        // Discover it first (within AOI) - the standard discovery path.
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(nearby), movementKind: null, nearby, CancellationToken.None);
        var discoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(discoveryPacket));

        // Now report the SAME actor at a position outside this session's own AOI (ordinary
        // incremental movement, e.g. a Moved/CellCrossed feed entry) - must vanish it for this
        // session, not continue projecting movement for an actor the client can no longer see.
        var farAway = nearby with { X = (ushort)(ViewerX + WorldVisibilityOptions.DefaultAreaSize + 5) };
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(farAway), WorldMonsterMovementKind.CellCrossed, farAway, CancellationToken.None);

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
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);
        var discoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(discoveryPacket));

        // A fresh snapshot that no longer contains this ActorId at all (vanished/reaped) - a second
        // reconciliation must vanish it for this session.
        projection.ApplySnapshot([], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);

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
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);
        await ReadDynamic(stream); // Discovery of the old life.

        // A fresh snapshot with the SAME ActorId but a DIFFERENT (new) IncarnationId - the old life
        // must be vanished first, never silently reused for the new life.
        var newIncarnation = oldIncarnation.Next();
        var newInstance = Alive(actorId, newIncarnation, x: ViewerX, y: ViewerY);
        projection.ApplySnapshot([newInstance], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);

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
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);
        await ReadDynamic(stream);

        // The map's own SimulationEpoch changed (World simulation rebuilt) - even though the SAME
        // ActorId+IncarnationId+position re-appears, the session's own prior-epoch view of it is
        // stale and must be vanished before being rediscovered under the new epoch.
        var newEpoch = WorldSimulationEpoch.NewEpoch();
        projection.ApplySnapshot([instance], newEpoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);

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

        var epoch = WorldSimulationEpoch.NewEpoch();
        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var instance = Alive(actorId, incarnation, x: ViewerX, y: ViewerY);

        // Both sessions discover the monster first.
        await attackerSession.NotifyMonsterMovedAsync(new WorldMonsterActorView(instance), movementKind: null, instance, CancellationToken.None);
        await ReadDynamic(attackerStream);
        await bystanderSession.NotifyMonsterMovedAsync(new WorldMonsterActorView(instance), movementKind: null, instance, CancellationToken.None);
        await ReadDynamic(bystanderStream);

        // Simulate the attacker's own local confirmed-kill path having ALREADY run synchronously
        // (its own death-vanish already sent via a different, existing code path in production) -
        // here that just means its own visible-tracker no longer has this actor, achieved directly
        // via the existing ForgetPlayer-shaped API this class already exposes for exactly this kind
        // of "this session no longer considers this actor visible" state.
        attackerSession.ForgetPlayer(actorId);

        // Now the World feed's own Died entry is fanned out to both sessions.
        var life = new WorldMonsterLifeReference(MapId, epoch, actorId, incarnation);
        await attackerSession.NotifyMonsterDiedAsync(life, CancellationToken.None);
        await bystanderSession.NotifyMonsterDiedAsync(life, CancellationToken.None);

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

        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(instance), movementKind: null, instance, CancellationToken.None);
        await ReadDynamic(stream);

        // This session's own generic tracker already says the actor is invisible (mirroring the
        // attacker's own already-cleared case) - NotifyMonsterDiedAsync must still run its own
        // monster-visibility cleanup rather than short-circuiting entirely.
        session.ForgetPlayer(actorId);
        await session.NotifyMonsterDiedAsync(new WorldMonsterLifeReference(MapId, epoch, actorId, oldIncarnation), CancellationToken.None);

        // A respawn under a NEW incarnation, same ActorId/position, reconciled via the ordinary full
        // reconciliation path - must be treated as a genuine fresh discovery (a stand entry is sent),
        // proving no stale _monsterVisibility entry for the OLD incarnation survived to interfere.
        var newIncarnation = oldIncarnation.Next();
        var respawned = Alive(actorId, newIncarnation, x: ViewerX, y: ViewerY);
        var projections = new MonsterFeedProjectionRegistry();
        var projection = projections.GetOrCreate(MapId);
        projection.ApplySnapshot([respawned], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);

        var rediscoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(rediscoveryPacket));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(rediscoveryPacket.AsSpan(5)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 7 substep 5, required test 2: walking discovery must use WorldMonsterInstance HP, not
    // any legacy local combat-state value - proven via deliberate divergence (World says 20/55,
    // there is no local combat-state entry at all, so any HP reaching the wire can only have come
    // from the WorldMonsterInstance itself).
    [Fact]
    public async Task NotifyMonsterMovedAsync_WalkingDiscovery_UsesWorldInstanceHp_NotLegacyLocalValue()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var walking = AliveWithHp(actorId, incarnation, x: ViewerX, y: ViewerY, currentHp: 20, maxHp: 55, isWalking: true, destinationX: (ushort)(ViewerX + 1), destinationY: ViewerY);

        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(walking), movementKind: null, walking, CancellationToken.None);

        var walkPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyMoveEntry, BinaryPrimitives.ReadInt16LittleEndian(walkPacket));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(walkPacket.AsSpan(5)));
        Assert.Equal(55, BinaryPrimitives.ReadInt32LittleEndian(walkPacket.AsSpan(79))); // maxHp
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(walkPacket.AsSpan(83))); // currentHp

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 7 substep 5, required test 3: an already-visible actor's WalkStarted movement projection
    // must also use WorldMonsterInstance HP.
    [Fact]
    public async Task NotifyMonsterMovedAsync_WalkStarted_AlreadyVisible_UsesWorldInstanceHp()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var standing = Alive(actorId, incarnation, x: ViewerX, y: ViewerY);
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(standing), movementKind: null, standing, CancellationToken.None);
        await ReadDynamic(stream); // Initial discovery.

        var walking = AliveWithHp(actorId, incarnation, x: ViewerX, y: ViewerY, currentHp: 12, maxHp: 55, isWalking: true, destinationX: (ushort)(ViewerX + 1), destinationY: ViewerY);
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(walking), WorldMonsterMovementKind.WalkStarted, walking, CancellationToken.None);

        var walkPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyMoveEntry, BinaryPrimitives.ReadInt16LittleEndian(walkPacket));
        Assert.Equal(55, BinaryPrimitives.ReadInt32LittleEndian(walkPacket.AsSpan(79))); // maxHp
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(walkPacket.AsSpan(83))); // currentHp

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 7 substep 5, required test 4: ReconcileMonsterVisibilityAsync's own full-snapshot
    // (re-)discovery path must use WorldMonsterInstance HP as well, never any stale local value -
    // proven by seeding a combat-state entry with a DIFFERENT (stale) HP than the fresh snapshot's
    // own instance carries, and asserting the resulting discovery packet encodes the fresh value.
    [Fact]
    public async Task ReconcileMonsterVisibilityAsync_Discovery_UsesWorldInstanceHp_NotStaleLegacyValue()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        // Legacy local store deliberately registered at FULL hp (55/55) - genuinely different from
        // the fresh World snapshot's own damaged value (28/55) below.
        combatState.Register(MapId, epoch, actorId, incarnation, maxHp: 55);
        var instance = AliveWithHp(actorId, incarnation, x: ViewerX, y: ViewerY, currentHp: 28, maxHp: 55);

        var projections = new MonsterFeedProjectionRegistry();
        var projection = projections.GetOrCreate(MapId);
        projection.ApplySnapshot([instance], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);

        var standPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(standPacket));
        Assert.Equal(55, BinaryPrimitives.ReadInt32LittleEndian(standPacket.AsSpan(73))); // maxHp
        Assert.Equal(28, BinaryPrimitives.ReadInt32LittleEndian(standPacket.AsSpan(77))); // currentHp - the FRESH World value, never the stale local 55.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 7 substep 5, required test 6: a HealthChanged transition on an ALREADY-VISIBLE actor
    // (movementKind: null, exactly what FanOutEntryAsync's generic non-Died tail passes for a
    // HealthChanged entry) must not synthesize any new discovery/HP packet - the local projection
    // is updated (proven separately below in test 7), but nothing is sent to the wire solely
    // because HP changed for an actor the client can already see.
    [Fact]
    public async Task NotifyMonsterMovedAsync_HealthChangedOnAlreadyVisibleActor_SendsNoUnsolicitedPacket()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var full = Alive(actorId, incarnation, x: ViewerX, y: ViewerY);
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(full), movementKind: null, full, CancellationToken.None);
        await ReadDynamic(stream); // Initial discovery.

        // The HP transition itself, projected exactly as FanOutEntryAsync's generic tail would for
        // a HealthChanged entry: same position, movementKind: null, only CurrentHp differs.
        var damaged = full with { CurrentHp = 30 };
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(damaged), movementKind: null, damaged, CancellationToken.None);

        // Confirmed via a harmless ping round-trip landing next instead of any HP/discovery packet.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 7 substep 5, required test 7: after a HealthChanged transition moves CurrentHp X -> Y for
    // an already-visible actor (no packet sent, per the previous test), a LATER natural
    // discovery/movement packet path must project Y, not the stale X - proven here via
    // ReconcileMonsterVisibilityAsync's own AOI-exit-then-rediscovery path, which is a genuine,
    // already-existing discovery trigger (never a fabricated one invented merely to make this test
    // easy).
    [Fact]
    public async Task NotifyMonsterMovedAsync_LaterDiscoveryAfterHealthChanged_ProjectsPostDamageHp()
    {
        var (client, stream, session, run) = await SetupViewerAsync();
        using var _ = client;

        var combatState = new MonsterCombatStateStore();
        var epoch = WorldSimulationEpoch.NewEpoch();
        var incarnation = WorldMonsterIncarnationId.First;
        const uint actorId = 1;
        var full = Alive(actorId, incarnation, x: ViewerX, y: ViewerY);
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(full), movementKind: null, full, CancellationToken.None);
        await ReadDynamic(stream); // Initial discovery at full HP.

        // HealthChanged: HP moves 55 -> 22 while already visible - no packet, per the previous test.
        var damaged = full with { CurrentHp = 22 };
        await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(damaged), movementKind: null, damaged, CancellationToken.None);
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        await ReadExact(stream, 2); // Drain the ping reply confirming no packet was sent for the HealthChanged itself.

        // Now the actor walks out of AOI and back in (a genuine, ordinary discovery trigger) -
        // ReconcileMonsterVisibilityAsync's own vanish-then-rediscover path.
        var outOfAoi = damaged with { X = (ushort)(ViewerX + WorldVisibilityOptions.DefaultAreaSize + 5) };
        var projections = new MonsterFeedProjectionRegistry();
        var projection = projections.GetOrCreate(MapId);
        projection.ApplySnapshot([outOfAoi], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);
        var vanishPacket = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));

        var backInAoi = damaged; // Same damaged (22/55) instance, back within AOI.
        projection.ApplySnapshot([backInAoi], epoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);

        var rediscoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(rediscoveryPacket));
        Assert.Equal(55, BinaryPrimitives.ReadInt32LittleEndian(rediscoveryPacket.AsSpan(73))); // maxHp
        Assert.Equal(22, BinaryPrimitives.ReadInt32LittleEndian(rediscoveryPacket.AsSpan(77))); // currentHp - the post-HealthChanged value, never the stale full 55.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
