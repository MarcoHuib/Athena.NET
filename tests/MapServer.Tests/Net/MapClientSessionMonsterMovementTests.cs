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
            [new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0))],
            allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
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
        await ReadDynamic(stream); // 0x0B32 skill list

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

        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: DateTimeOffset.UnixEpoch, jitterMs: () => 0));

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.WalkStarted), MonsterCombatState.FromInstance(target), CancellationToken.None);

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

        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: DateTimeOffset.UnixEpoch, jitterMs: () => 0));

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.CellCrossed), MonsterCombatState.FromInstance(target), CancellationToken.None);

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

        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: DateTimeOffset.UnixEpoch, jitterMs: () => 0));
        target.AdvanceMovement(DateTimeOffset.UnixEpoch.AddMilliseconds(400));
        Assert.False(target.IsWalking);

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.WalkFinished), MonsterCombatState.FromInstance(target), CancellationToken.None);

        await AssertNothingArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 4 (IMonsterActorView/MonsterCombatState split) regression coverage: NotifyMonsterMovedAsync
    // now takes MonsterMovementChange.Instance as IMonsterActorView (not MobInstance directly) and
    // reads HP from the separately-supplied MonsterCombatState, never by casting Instance back to
    // MobInstance - these tests prove that plumbing actually reaches the wire packet correctly,
    // both for the full-HP sentinel and a damaged monster, using the SAME production construction
    // path (MonsterCombatState.FromInstance) every real call site uses.

    [Fact]
    public void MobInstance_SatisfiesIMonsterActorView_ExposingRealIncarnationIdAndPositionData()
    {
        var allocator = new WorldActorIdAllocator();
        var registry = new MonsterRegistry(
            [new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0))],
            allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
        var target = registry.AllInstances[0];
        IMonsterActorView actor = target; // Compiles without any cast helper - MobInstance implements the interface directly.

        Assert.Equal(target.ActorId, actor.ActorId);
        Assert.Equal(target.IncarnationId, actor.IncarnationId);
        Assert.Equal(MonsterIncarnationId.First, actor.IncarnationId); // The REAL incarnation, not a stub.
        Assert.Equal(target.Map, actor.Map);
        Assert.Equal(target.GetPosition(), actor.GetPosition());
        Assert.Equal(target.Spawn.Mob.Id, actor.MobId);
        Assert.Equal(target.Spawn.Mob.Name, actor.Name);
        Assert.Equal(target.Spawn.Mob.WalkSpeed, actor.WalkSpeed);
        Assert.Equal(target.IsWalking, actor.IsWalking);
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_WalkStarted_FullHp_SendsSentinelHpValues()
    {
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;
        await MakeVisibleAsync(stream, target);

        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: DateTimeOffset.UnixEpoch, jitterMs: () => 0));

        var combat = MonsterCombatState.FromInstance(target);
        Assert.Equal(55u, combat.CurrentHp);
        Assert.Equal(55u, combat.MaxHp);
        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.WalkStarted), combat, CancellationToken.None);

        var walkPacket = await ReadDynamic(stream);
        Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(walkPacket));
        // Full-HP sentinel (0xFFFFFFFF/0xFFFFFFFF) at the same offsets IroMonsterActorPacketsTests
        // proves for BuildWalkEntry directly - this confirms MonsterCombatState's own values
        // actually reach that builder unchanged through NotifyMonsterMovedAsync's plumbing.
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(walkPacket.AsSpan(79)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(walkPacket.AsSpan(83)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_WalkStarted_DamagedMonster_SendsRealHpValues()
    {
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;
        await MakeVisibleAsync(stream, target);

        target.ApplyDamage(37); // 55 -> 18 current HP.
        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: DateTimeOffset.UnixEpoch, jitterMs: () => 0));

        var combat = MonsterCombatState.FromInstance(target);
        Assert.Equal(18u, combat.CurrentHp);
        Assert.Equal(55u, combat.MaxHp);
        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.WalkStarted), combat, CancellationToken.None);

        var walkPacket = await ReadDynamic(stream);
        Assert.Equal(55, BinaryPrimitives.ReadInt32LittleEndian(walkPacket.AsSpan(79)));
        Assert.Equal(18, BinaryPrimitives.ReadInt32LittleEndian(walkPacket.AsSpan(83)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_DiscoveryStandEntry_DamagedMonster_SendsRealHpValues()
    {
        // Discovery (not-yet-visible) path: MakeVisibleAsync is deliberately NOT called first, so
        // the very first NotifyMonsterMovedAsync call takes the "just became visible" branch and
        // builds a fresh BuildStandEntry - exercising the OTHER packet-building call site that
        // reads MonsterCombatState (distinct from the already-visible WalkStarted path above).
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;
        target.ApplyDamage(40); // 55 -> 15 current HP.

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.CellCrossed), MonsterCombatState.FromInstance(target), CancellationToken.None);

        var standPacket = await ReadDynamic(stream);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(standPacket));
        Assert.Equal(55, BinaryPrimitives.ReadInt32LittleEndian(standPacket.AsSpan(73)));
        Assert.Equal(15, BinaryPrimitives.ReadInt32LittleEndian(standPacket.AsSpan(77)));

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
        var spawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawn], allocator.Allocate, new FixedCellSelector(70, 51), clock);
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
                    await session.NotifyMonsterMovedAsync(change, MonsterCombatState.FromInstance(target), CancellationToken.None);
                    var walkPacket = await ReadDynamic(stream);
                    Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(walkPacket));
                }
                else
                {
                    await session.NotifyMonsterMovedAsync(change, MonsterCombatState.FromInstance(target), CancellationToken.None);
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
                await session.NotifyMonsterMovedAsync(change, MonsterCombatState.FromInstance(target), CancellationToken.None);
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

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.CellCrossed), MonsterCombatState.FromInstance(target), CancellationToken.None);

        var standPacket = await ReadDynamic(stream);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(standPacket));
        Assert.Equal((byte)5, standPacket[4]); // NPC_MOB_TYPE
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(standPacket.AsSpan(5)));

        // A second notification for the SAME still-visible instance must not resend a duplicate
        // discovery packet - only the first crossing into visibility does.
        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.CellCrossed), MonsterCombatState.FromInstance(target), CancellationToken.None);
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

        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], orthogonalStepMs: 400, now: DateTimeOffset.UnixEpoch, jitterMs: () => 0));

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(target, MonsterMovementChangeKind.WalkStarted), MonsterCombatState.FromInstance(target), CancellationToken.None);

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
        var farSpawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([farSpawn], allocator.Allocate, new FixedCellSelector(275, 275), TimeProvider.System);
        var farTarget = registry.AllInstances[0];

        var (client, stream, session, run, _) = await SetupAsync(sharedTarget: farTarget, sharedRegistry: registry);
        using var _2 = client;

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(farTarget, MonsterMovementChangeKind.CellCrossed), MonsterCombatState.FromInstance(farTarget), CancellationToken.None);

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
        var spawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, RespawnDelay: 5000, RespawnRandomDelay: 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawn], allocator.Allocate, new FixedCellSelector(75, 51), clock);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var target = registry.AllInstances[0];
        var combatState = new MonsterCombatStateStore();
        combatState.Register(target.Map, target);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules(), combatState);

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
            accountId: AccountId, charId: CharId, monsters: registry, combat: combat, timeProvider: clock, combatState: combatState);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));
        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream); // 0x0B32 skill list
        await MakeVisibleAsync(stream, target);

        var attackPacket = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(attackPacket, PacketConstants.IroCzAttackRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(attackPacket.AsSpan(2), target.ActorId);
        attackPacket[6] = 7; // DMG_REPEAT
        attackPacket[7] = 0x7f;
        await WriteBoundedAsync(stream, attackPacket);

        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        // ZC_HP_INFO (0x0977) follows the killing blow itself (with hp=0) before the vanish
        // packet - see PacketConstants.ZcHpInfo's own doc comment for the pinned ordering trace.
        var hpInfoPacket = await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        Assert.Equal((short)PacketConstants.ZcHpInfo, BinaryPrimitives.ReadInt16LittleEndian(hpInfoPacket));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(6)));
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
            await session.NotifyMonsterMovedAsync(new MonsterMovementChange(instance, MonsterMovementChangeKind.CellCrossed), MonsterCombatState.FromInstance(instance), CancellationToken.None);

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
        var otherMapSpawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land04", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([otherMapSpawn], allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
        var otherMapTarget = registry.AllInstances[0];

        var (client, stream, session, run, _) = await SetupAsync(sharedTarget: otherMapTarget, sharedRegistry: registry);
        using var _2 = client;

        await session.NotifyMonsterMovedAsync(new MonsterMovementChange(otherMapTarget, MonsterMovementChangeKind.CellCrossed), MonsterCombatState.FromInstance(otherMapTarget), CancellationToken.None);

        await AssertNothingArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Small, BOUNDED wiring test: NotifyMonsterMovedAsync correctly delegates visibility tracking
    // to VisibleActorTracker for a modest batch of concurrent discovery calls - the actual
    // concurrency INVARIANT (exactly-once discovery under real thread contention, no corruption)
    // is proven separately and deterministically by VisibleActorTrackerTests, which needs no TCP
    // session, no background reader, and no real-time hammering window at all. This test exists
    // only to prove the WIRING (MapClientSession actually calls into the tracker correctly, one
    // discovery packet per actor reaches the wire) - it deliberately does NOT try to reproduce the
    // concurrency race itself, matching the split this project's own CI-flakiness investigation
    // called for (mixing a real concurrency invariant with uncontrolled TCP backpressure/load
    // timing was the actual source of the earlier flaky test's OperationCanceledException
    // failures under CI load).
    [Fact]
    public async Task NotifyMonsterMovedAsync_ConcurrentCallsForManyInstances_EachDiscoveredExactlyOnce()
    {
        const int monsterCount = 12;
        var allocator = new WorldActorIdAllocator();
        var spawns = Enumerable.Range(0, monsterCount)
            .Select(i => new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", i)))
            .ToArray();
        var positions = Enumerable.Range(0, monsterCount).Select(i => (ushort)(68 + i)).ToArray();
        var registry = new MonsterRegistry(spawns, allocator.Allocate, new SequentialCellSelector(positions), TimeProvider.System);
        var instances = registry.AllInstances;

        var (client, stream, session, run, _) = await SetupAsync(sharedRegistry: registry);
        using var _ = client;

        var discoveryCountsByActorId = new ConcurrentDictionary<uint, int>();
        var readerFailures = new ConcurrentBag<Exception>();
        var expectedPacketCount = monsterCount; // Exactly one discovery packet per instance.
        var received = 0;
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (received < expectedPacketCount)
                {
                    var header = await ReadExact(stream, 4);
                    var packetId = BinaryPrimitives.ReadInt16LittleEndian(header);
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                    var rest = length > 4 ? await ReadExact(stream, length - 4) : [];
                    if (packetId == 0x09ff || packetId == 0x09fd)
                    {
                        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(1));
                        discoveryCountsByActorId.AddOrUpdate(actorId, 1, (_, count) => count + 1);
                        if (Interlocked.Increment(ref received) >= expectedPacketCount) allReceived.TrySetResult();
                    }
                }
            }
            catch (Exception ex)
            {
                readerFailures.Add(ex);
                allReceived.TrySetException(ex);
            }
        });

        // A modest number of concurrent callers (not an unbounded real-time hammer) each notifying
        // ALL instances at once - the exact shape of MapTcpServer's own tick-loop fan-out, run
        // concurrently to exercise TryMarkVisible's own race window, bounded by a fixed loop count
        // rather than a wall-clock duration.
        var callers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            foreach (var instance in instances)
            {
                try { await session.NotifyMonsterMovedAsync(new MonsterMovementChange(instance, MonsterMovementChangeKind.CellCrossed), MonsterCombatState.FromInstance(instance), CancellationToken.None); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }));

        await Task.WhenAll(callers);
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await readerTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(readerFailures);
        foreach (var instance in instances)
            Assert.Equal(1, discoveryCountsByActorId.GetValueOrDefault(instance.ActorId));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
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
