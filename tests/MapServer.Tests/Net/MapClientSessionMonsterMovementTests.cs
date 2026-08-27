using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.GameData.Quests;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// End-to-end proof of MapClientSession.NotifyMonsterMovedAsync (called by MapTcpServer's shared
// monster tick loop in production - see that method's own doc comment): only WalkStarted sends a
// packet to an already-visible session (the capture-verified 0x09FD walk entry). CellCrossed AND
// WalkFinished both send NOTHING - pinned unit_walktoxy_nextcell's ordinary per-cell continuation
// never resends the walk packet, and reaching the end of the walkpath sends nothing at all (no
// clif_fixpos) for an ordinary completed walk; see that method's own doc comment for why the
// captured 0x0088 (which occurs in a COMBAT sequence, not an ordinary walk completion) does not
// apply here. A monster that just walked into a stationary session's own 14-cell range gets a
// fresh discovery packet exactly once (0x09FD if still walking, 0x09FF if not), and a monster
// still out of range or on a different map gets nothing.
public sealed class MapClientSessionMonsterMovementTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    private sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
    {
        public bool TrySelectCell(MobSpawnDefinition spawn, int index, out MobPosition position)
        {
            position = new MobPosition(x, y);
            return true;
        }
    }

    // A disconnected test session's default gameplayStatePersistence (charConnector) always fails
    // GetAsync, which makes CompleteIroAuthenticationAsync call HandleAuthFail() and never send
    // the bootstrap burst this test's setup depends on - matching the exact trap
    // MapClientSession's own test-facing-constructor doc comment warns about. Every test in this
    // file supplies this fixture explicitly instead.
    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private static CharacterGameplayState FreshNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    // Unarmed (no equipped weapon is set up in this file's tests), so WeaponAttackCalculator's own
    // `if (weapon is not null)` guard means an override on the weapon-ATK roll has NO effect
    // unarmed - statusAtk (str/level-driven) is the only damage source, so a high-level/high-STR
    // attacker is required to one-shot G_PORING's 55 HP unarmed, matching
    // MapClientSessionMonsterCombatTests' own StrongNovice fixture for the same reason.
    private static CharacterGameplayState StrongNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 99, JobLevel: 10,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 100, CurrentSp: 100, MaxHp: 100, MaxSp: 100,
        StatPoints: 0, SkillPoints: 0, Strength: 99, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 99, Luck: 99);

    // Every socket read in this file goes through this one helper - previous CI hangs were caused
    // by async network tests waiting forever on a stream that stopped answering. Bounded with an
    // explicit CancellationTokenSource (not a bare .WaitAsync wrapper around the call) so the
    // underlying ReadExactlyAsync operation itself is cancelled rather than merely abandoned - a
    // timeout here throws (failing the test loudly), it is never treated as "nothing arrived"; see
    // AssertNothingArrivesAsync's own doc comment for how "nothing arrived" is actually proven.
    private static readonly TimeSpan SocketTimeout = TimeSpan.FromSeconds(5);

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var cts = new CancellationTokenSource(SocketTimeout);
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static async Task WriteBoundedAsync(Stream stream, byte[] payload)
    {
        using var cts = new CancellationTokenSource(SocketTimeout);
        await stream.WriteAsync(payload, cts.Token);
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target)> SetupAsync(MobInstance? sharedTarget = null, MonsterRegistry? sharedRegistry = null, MonsterRuntime? monsterRuntime = null)
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
        var registry = sharedRegistry ?? new MonsterRegistry(
            [new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0))],
            allocator, new FixedCellSelector(75, 51), TimeProvider.System);
        var target = sharedTarget ?? registry.AllInstances[0];

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: new FixedGameplayStatePersistence(FreshNovice()),
            accountId: AccountId, charId: CharId, monsters: registry, monsterRuntime: monsterRuntime);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));

        // Consume the fixed iRO bootstrap burst (0x0B18/0x0283/0x0ADE/0x02EB) - no inventory
        // packets follow here since no inventory list persistence override was supplied for this
        // narrowly-scoped movement-notification test.
        await ReadExact(stream, 4 + 6 + 6 + 13);

        return (client, stream, session, run, target);
    }

    private static async Task MakeVisibleAsync(Stream stream, MobInstance target)
    {
        await WriteBoundedAsync(stream, [0x7d, 0x00, 0xaa]);
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        await ReadExact(stream, 4);  // 0x0B0B inventoryEnd
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));
        Assert.Equal(target.ActorId, actorId);
    }

    // Ping round-trip as the ordering barrier: no real-time wait is needed, since nothing
    // server-side can produce more bytes without another client request - sending 0x0B1C and
    // reading exactly ONE short reply proves the server has caught up with everything sent so far.
    // The read is bounded (via ReadExact's own CancellationTokenSource), so a session that stops
    // answering fails this test loudly instead of hanging; "timed out" and "received something
    // unexpected" are both real failures here, never silently treated as "nothing arrived". The
    // first 2 bytes read ARE the reply's own packet id (ZcPingLive is a fixed, header-only short
    // reply - see SendPingLiveAsync) - if an unexpected movement packet arrived first instead, its
    // own (different) leading id fails the Assert.Equal below rather than being misinterpreted as
    // a partial ping reply.
    private static async Task AssertNothingArrivesAsync(Stream stream)
    {
        await WriteBoundedAsync(stream, [0x1c, 0x0b]);
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_WalkStarted_AlreadyVisible_SendsWalkEntryOnTheWire()
    {
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;
        await MakeVisibleAsync(stream, target);

        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: 1, nowOffset: DateTimeOffset.UnixEpoch, jitterMs: () => 0));

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.WalkStarted), CancellationToken.None);

        var walkPacket = await ReadDynamic(stream);
        Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(walkPacket));
        Assert.Equal((byte)5, walkPacket[4]); // NPC_MOB_TYPE
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(walkPacket.AsSpan(5)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Pinned unit_walktoxy_nextcell's ordinary per-cell continuation always passes sendMove=false
    // (unit.cpp:749) - only the initial unit_walktoxy call passes sendMove=true (unit.cpp:317).
    // A CellCrossed change must therefore NEVER put a fresh 0x09FD on the wire.
    [Fact]
    public async Task NotifyMonsterMovedAsync_CellCrossed_AlreadyVisible_SendsNothing()
    {
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;
        await MakeVisibleAsync(stream, target);

        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: 1, nowOffset: DateTimeOffset.UnixEpoch, jitterMs: () => 0));

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.CellCrossed), CancellationToken.None);

        await AssertNothingArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Pinned unit_walktoxy_nextcell reaching the end of the walkpath (ud->walkpath.path_pos >=
    // path_len) simply returns false - no clif_fixpos, no stop notification, for either a PC or a
    // MOB (unit.cpp:186-192). An ordinary completed idle walk is silent on the wire past its own
    // initial 0x09FD; the captured 0x0088 (frame 674) occurs in a combat sequence (the Poring's own
    // attack-back), not as evidence that every natural walk completion sends it.
    [Fact]
    public async Task NotifyMonsterMovedAsync_WalkFinished_AlreadyVisible_SendsNothing()
    {
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;
        await MakeVisibleAsync(stream, target);

        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: 1, nowOffset: DateTimeOffset.UnixEpoch, jitterMs: () => 0));
        target.AdvanceMovement(DateTimeOffset.UnixEpoch.AddMilliseconds(400));
        Assert.False(target.IsWalking);

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.WalkFinished), CancellationToken.None);

        await AssertNothingArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Full lifecycle, driven by the REAL MonsterRuntime scheduler (not a hand-picked
    // MonsterMovementChange) over a complete idle walk from start to finish: exactly one 0x09FD
    // must appear on the wire (the walk's own WalkStarted) and zero 0x0088 packets, no matter how
    // many CellCrossed/WalkFinished ticks occur along the way.
    [Fact]
    public async Task CompleteNormalIdleWalk_ProducesExactlyOneWalkEntryAndZeroStopMovePackets()
    {
        var clock = new Athena.Net.MapServer.Tests.World.FakeTimeProvider();
        var allocator = new WorldActorIdAllocator();
        // The session's own fixed player position (SetupAsync's "int_land03", 75, 51) must be
        // within the 14-cell visibility range of the mob's spawn cell for MakeVisibleAsync's
        // initial 0x007D spawn read to ever complete.
        var spawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawn], allocator, new FixedCellSelector(70, 51), clock);
        var target = registry.AllInstances[0];
        var map = new MapCollisionMap("int_land03", 90, 90, Enumerable.Repeat(MapCellFlags.Walkable, 90 * 90).ToArray());
        var collisionProvider = new MapCollisionProvider([map]);
        var pathProvider = new RathenaCompatibleMovementPathProvider(collisionProvider);
        var monsterRuntime = new MonsterRuntime(registry, collisionProvider, pathProvider, clock);

        var (client, stream, session, run, _) = await SetupAsync(sharedTarget: target, sharedRegistry: registry, monsterRuntime: monsterRuntime);
        using var _2 = client;
        await MakeVisibleAsync(stream, target);

        // Only WalkStarted ever produces a wire packet (0x09FD) from an already-visible session -
        // see NotifyMonsterMovedAsync's own doc comment; CellCrossed/WalkFinished are both no-ops.
        // Reading exactly one dynamic packet per observed WalkStarted (and nothing for any other
        // Kind) is therefore no different from reading the wire directly - it avoids a fragile
        // "drain until a ping proves silence" loop while still proving the REAL scheduler's own
        // change sequence over a full walk never produces more than that.
        var walkStartedCount = 0;

        // Drive the real scheduler until a walk both starts and completes (bounded iteration count
        // as a test-failure guard, never a synchronization mechanism this test relies on).
        for (var i = 0; i < 40 && walkStartedCount == 0; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            var changes = monsterRuntime.ProcessTick();
            foreach (var change in changes)
            {
                if (change.Kind == MonsterMovementChangeKind.WalkStarted)
                {
                    walkStartedCount++;
                    await session.NotifyMonsterMovedAsync(change, CancellationToken.None);
                    var walkPacket = await ReadDynamic(stream);
                    Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(walkPacket));
                }
                else
                {
                    await session.NotifyMonsterMovedAsync(change, CancellationToken.None);
                }
            }
        }
        Assert.Equal(1, walkStartedCount);
        Assert.True(target.IsWalking);

        for (var i = 0; i < 200 && target.IsWalking; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(400));
            var changes = monsterRuntime.ProcessTick();
            foreach (var change in changes)
            {
                // A second WalkStarted would mean a new walk began before this test finished
                // observing the first one's own completion - not expected here, and would also
                // desynchronize the dynamic-packet read count above.
                Assert.NotEqual(MonsterMovementChangeKind.WalkStarted, change.Kind);
                await session.NotifyMonsterMovedAsync(change, CancellationToken.None);
            }
        }
        Assert.False(target.IsWalking);

        // Nothing else must have reached the wire for any CellCrossed/WalkFinished change - in
        // particular, zero 0x0088 (ZC_STOPMOVE) packets for this ordinary completed walk.
        await AssertNothingArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_MonsterWalksIntoRangeOfAStationaryPlayer_NotWalking_SendsAFreshStandEntry()
    {
        // Session never sends 0x007D in this test - proving discovery does not depend on the
        // player's own map-load/movement re-scan (a monster moving INTO visibility: nothing else
        // re-checks GetVisibleInstances for a player who never moves).
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.CellCrossed), CancellationToken.None);

        var standPacket = await ReadDynamic(stream);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(standPacket));
        Assert.Equal((byte)5, standPacket[4]); // NPC_MOB_TYPE
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(standPacket.AsSpan(5)));

        // A second notification for the SAME still-visible instance must not resend a duplicate
        // discovery packet - only the first crossing into visibility does.
        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.CellCrossed), CancellationToken.None);
        await AssertNothingArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Pinned clif_spawn dispatches to clif_set_unit_walking (not clif_set_unit_idle) when the
    // discovered unit is already mid-walk - a monster found WHILE walking must get the walking
    // (0x09FD) layout, not a plain stand entry, and land at its CURRENT cell with its real
    // in-flight destination (never a fabricated one).
    [Fact]
    public async Task NotifyMonsterMovedAsync_MonsterWalksIntoRangeOfAStationaryPlayer_WhileWalking_SendsWalkEntryNotStandEntry()
    {
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;

        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: 1, nowOffset: DateTimeOffset.UnixEpoch, jitterMs: () => 0));

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.WalkStarted), CancellationToken.None);

        var discoveryPacket = await ReadDynamic(stream);
        Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(discoveryPacket));
        Assert.Equal((byte)5, discoveryPacket[4]); // NPC_MOB_TYPE
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(discoveryPacket.AsSpan(5)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_MonsterOutOfRangeAndNotYetVisible_SendsNothing()
    {
        var allocator = new WorldActorIdAllocator();
        // 200 cells away - far outside the 14-cell visibility range used by both
        // MonsterRegistry.GetVisibleInstances and NotifyMonsterMovedAsync's own discovery check.
        var farSpawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([farSpawn], allocator, new FixedCellSelector(275, 275), TimeProvider.System);
        var farTarget = registry.AllInstances[0];

        var (client, stream, session, run, _) = await SetupAsync(sharedTarget: farTarget, sharedRegistry: registry);
        using var _2 = client;

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(farTarget, MonsterMovementChangeKind.CellCrossed), CancellationToken.None);

        await AssertNothingArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Full kill -> respawn -> visible-again integration: kills the monster through the REAL attack
    // wire path (exercising MapClientSession's own production vanish-on-death _visibleActorIds.Remove
    // - see that call site's own comment), then reproduces the actual sequence MapTcpServer's shared
    // tick loop performs on respawn (MonsterRegistry.ProcessDueRespawns() returning WHICH instances
    // respawned, then fanning each one out via NotifyMonsterMovedAsync using a CellCrossed-kind
    // change - see MapTcpServer.RunMonsterTickLoopAsync's own doc comment for why CellCrossed is
    // deliberately used here, not WalkStarted) - proving the session's own "not yet visible, but now
    // in range" discovery path resolves it to a plain 0x09FF stand entry. Critically this does NOT
    // wait for MonsterRuntime.ProcessTick/idle-walk AI at all - MIN_RANDOMWALKTIME is 4000ms+jitter,
    // and this test's clock only advances by the respawn delay itself, proving the client becomes
    // able to see the respawned monster again without relying on an accidental idle walk.
    [Fact]
    public async Task KillThenRespawn_InsideVisibilityRange_MakesTheInstanceVisibleAgain_WithoutWaitingForIdleWalk()
    {
        // ControllableTimeProvider (not the plain World.FakeTimeProvider that only overrides
        // GetUtcNow) is required here: MapClientSession's repeat-attack loop schedules its next hit
        // via Task.Delay(delay, TimeProvider, ...), which calls TimeProvider.CreateTimer - a
        // TimeProvider that doesn't override CreateTimer falls back to REAL wall-clock timers
        // regardless of what GetUtcNow() reports, which would let a second, unwanted real hit fire
        // against an already-dead target while this test is busy asserting on the first one.
        // ControllableTimeProvider's own CreateTimer only ever fires from an explicit AdvanceAsync
        // call, so no hit beyond the one this test deliberately drives can ever occur.
        var clock = new Athena.Net.MapServer.Tests.Testing.ControllableTimeProvider();
        var allocator = new WorldActorIdAllocator();
        var spawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, RespawnDelayMs: 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawn], allocator, new FixedCellSelector(75, 51), clock);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules());
        var target = registry.AllInstances[0];

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        using var _ = client;
        var stream = client.GetStream();

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: new FixedGameplayStatePersistence(StrongNovice()),
            accountId: AccountId, charId: CharId, monsters: registry, combat: combat, timeProvider: clock);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));
        await ReadExact(stream, 4 + 6 + 6 + 13);
        await MakeVisibleAsync(stream, target);

        var attackPacket = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(attackPacket, PacketConstants.IroCzAttackRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(attackPacket.AsSpan(2), target.ActorId);
        attackPacket[6] = 7; // DMG_REPEAT
        attackPacket[7] = 0x7f;
        await WriteBoundedAsync(stream, attackPacket);

        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        var vanishPacket = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.False(target.IsAlive);

        registry.ScheduleRespawnIfNeeded(target);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(5001));
        var respawned = registry.ProcessDueRespawns();
        Assert.Single(respawned);
        Assert.True(target.IsAlive);
        Assert.False(target.IsWalking); // Proves discovery isn't riding along on an idle walk.

        foreach (var instance in respawned)
            await session.NotifyMonsterMovedAsync(new MonsterMovementChange(instance, MonsterMovementChangeKind.CellCrossed), CancellationToken.None);

        var standPacket = await ReadDynamic(stream);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(standPacket));
        Assert.Equal((byte)5, standPacket[4]); // NPC_MOB_TYPE
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(standPacket.AsSpan(5)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_MonsterOnADifferentMap_SendsNothing()
    {
        var allocator = new WorldActorIdAllocator();
        var otherMapSpawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land04", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([otherMapSpawn], allocator, new FixedCellSelector(75, 51), TimeProvider.System);
        var otherMapTarget = registry.AllInstances[0];

        var (client, stream, session, run, _) = await SetupAsync(sharedTarget: otherMapTarget, sharedRegistry: registry);
        using var _2 = client;

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(otherMapTarget, MonsterMovementChangeKind.CellCrossed), CancellationToken.None);

        await AssertNothingArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Concurrency regression for VisibleActorTracker (MapClientSession's own thread-safe wrapper
    // around _visibleActorIds - see that type's own doc comment for why a plain HashSet<uint>
    // stopped being safe once MapTcpServer's shared monster tick loop began calling
    // NotifyMonsterMovedAsync concurrently with this session's other visibility-touching call
    // sites). This test hammers the SAME session from many concurrent tasks performing exactly
    // those operations at once for several seconds:
    //   - repeated discovery/re-discovery of a batch of monster instances via NotifyMonsterMovedAsync
    //     (the exact call MapTcpServer's tick loop makes, many instances/many overlapping calls);
    //   - repeated 0x007D map-loaded packets from the client, which Clear() the visibility set and
    //     re-populate it via SendVisibleMonsterActorsAsync (the exact "warp/map-change Clear" and
    //     "player movement visibility scan" call sites);
    //   - repeated 0x0368 actor-info requests (the "actor-info handling" call site), reading
    //     IsActorVisible concurrently with all of the above.
    // A HashSet<uint> under this exact concurrent read/Add/Remove/Clear pattern either throws
    // (InvalidOperationException from a corrupted internal bucket structure) or produces duplicate
    // discovery packets for the same actor within one visibility "generation" (two racing callers
    // both observing "not yet visible" and both sending a stand/walk entry) - this test proves
    // neither happens under real concurrent load, only under the VisibleActorTracker fix.
    [Fact]
    public async Task VisibleActorTracker_ConcurrentDiscoveryRemovalAndClear_ProducesNoCorruptionOrDuplicateDiscovery()
    {
        const int monsterCount = 12;
        var allocator = new WorldActorIdAllocator();
        var spawns = Enumerable.Range(0, monsterCount)
            .Select(i => new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", i)))
            .ToArray();
        // Distinct cells all within the player's (75,51) 14-cell visibility range.
        var positions = Enumerable.Range(0, monsterCount).Select(i => (ushort)(68 + i)).ToArray();
        var registry = new MonsterRegistry(spawns, allocator, new SequentialCellSelector(positions), TimeProvider.System);
        var instances = registry.AllInstances;

        var (client, stream, session, run, _) = await SetupAsync(sharedRegistry: registry);
        using var _ = client;

        var actorIdsById = instances.ToDictionary(i => i.ActorId);
        var discoveryCountsByActorId = new ConcurrentDictionary<uint, int>();
        var readerFailures = new ConcurrentBag<Exception>();
        var stop = new CancellationTokenSource();

        // Background reader: drains every dynamic packet (0x09FF/0x09FD stand/walk entries) plus
        // whatever else arrives (0x0ADF actor-name replies from the concurrent 0x0368 requests) for
        // the duration of the hammering, tallying discovery packets per actor ID. Reads are
        // deliberately NOT individually bounded here (unlike this file's other helpers): the reader
        // is racing an explicit `stop` signal, not waiting for a specific reply, so a real hang
        // would show up as the overall test timing out via xunit's own test-method timeout rather
        // than silently passing - the requirement this file's helpers protect against is a
        // synchronization primitive being misread as proof of silence, which does not apply to a
        // best-effort drain loop that is cancelled unconditionally at the end regardless of outcome.
        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    byte[] header;
                    try { header = await ReadExact(stream, 4); }
                    catch (OperationCanceledException) { return; }
                    catch (IOException) { return; }
                    var packetId = BinaryPrimitives.ReadInt16LittleEndian(header);
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                    var rest = length > 4 ? await ReadExact(stream, length - 4) : [];

                    if (packetId == 0x09ff || packetId == 0x09fd)
                    {
                        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(1));
                        discoveryCountsByActorId.AddOrUpdate(actorId, 1, (_, count) => count + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                readerFailures.Add(ex);
            }
        });

        // Concurrent hammering for a bounded real-time window: several independent tasks
        // repeatedly performing the exact operations that raced on the old plain HashSet<uint>.
        var hammerDuration = TimeSpan.FromSeconds(2);
        var deadline = DateTime.UtcNow + hammerDuration;
        var hammerTasks = new List<Task>();

        // Tasks 1-4: repeatedly notify ALL instances as "moved" (CellCrossed - the discovery Kind;
        // see NotifyMonsterMovedAsync's own doc comment) concurrently with each other - the exact
        // shape of MapTcpServer's own tick-loop fan-out, run by 4 concurrent "ticks" at once to
        // force the race the old HashSet<uint> could not survive.
        for (var t = 0; t < 4; t++)
        {
            hammerTasks.Add(Task.Run(async () =>
            {
                while (DateTime.UtcNow < deadline)
                {
                    foreach (var instance in instances)
                    {
                        try
                        {
                            await session.NotifyMonsterMovedAsync(new MonsterMovementChange(instance, MonsterMovementChangeKind.CellCrossed), CancellationToken.None);
                        }
                        catch (IOException) { return; }
                        catch (ObjectDisposedException) { return; }
                    }
                }
            }));
        }

        // Task 5: repeatedly sends 0x007D (Clear() + SendVisibleMonsterActorsAsync re-discovery) -
        // the "warp/map-change Clear" and "player movement visibility scan" call sites, racing
        // directly against the discovery tasks above on the SAME underlying set.
        hammerTasks.Add(Task.Run(async () =>
        {
            while (DateTime.UtcNow < deadline)
            {
                try { await WriteBoundedAsync(stream, [0x7d, 0x00, 0xaa]); }
                catch (IOException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        }));

        // Task 6: repeatedly sends 0x0368 actor-info requests for a random subset of actors - the
        // "actor-info handling" call site, reading IsActorVisible concurrently with all of the above.
        hammerTasks.Add(Task.Run(async () =>
        {
            var random = new Random(12345);
            while (DateTime.UtcNow < deadline)
            {
                var actorId = instances[random.Next(instances.Count)].ActorId;
                var packet = new byte[7];
                BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzActorInfoRequest);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId);
                try { await WriteBoundedAsync(stream, packet); }
                catch (IOException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        }));

        await Task.WhenAll(hammerTasks);
        stop.Cancel();
        try { await readerTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } catch (TimeoutException) { }

        Assert.Empty(readerFailures);

        client.Close();
        // A write racing the client-side close (e.g. NotifyMonsterMovedAsync's own WriteAsync,
        // still catching up on the last hammer iteration when the socket closes) is an expected,
        // ordinary teardown race - not the corruption this test is checking for - so a resulting
        // IOException/SocketException on the session's own RunAsync task is tolerated here exactly
        // like every other test in this file that closes the client while server-side work may
        // still be in flight.
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException) { }
        catch (SocketException) { }

        // The actual regression check: HashSet<uint> corruption under this exact concurrent
        // pattern manifests as either a thrown exception during the hammering (caught above as a
        // readerFailure or an unhandled task fault the awaited Task.WhenAll would have surfaced)
        // or a discovery packet count that doesn't line up with "at least one, plausibly several
        // across repeated 0x007D Clear() cycles, but never absent" for any actor that was ever in
        // range the whole time. Every one of the monsterCount actors (all placed within visibility
        // range for the session's entire lifetime) must have been discovered at least once.
        foreach (var instance in instances)
        {
            Assert.True(discoveryCountsByActorId.TryGetValue(instance.ActorId, out var count) && count > 0,
                $"actorId={instance.ActorId} was never discovered despite being in range for the whole hammering window.");
        }
    }

    private sealed class SequentialCellSelector(ushort[] cells) : IMobSpawnCellSelector
    {
        private int _index;
        public bool TrySelectCell(MobSpawnDefinition spawn, int index, out MobPosition position)
        {
            var x = cells[_index++ % cells.Length];
            position = new MobPosition(x, 51);
            return true;
        }
    }
}
