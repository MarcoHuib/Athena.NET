using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.GameData.Quests;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Live-bug regression (mob idle random walk continuing during combat engagement): proves the
// REAL production orchestration - MonsterEngagementTickProcessor, the exact type
// MapTcpServer.RunMonsterTickLoopAsync constructs and calls every tick - correctly wires
// MonsterEngagementDomain's decisions through to a real MapClientSession over the real wire
// protocol. This is deliberately NOT a hand-written reimplementation of the orchestration
// algorithm: every test below constructs the same MonsterEngagementTickProcessor production type
// and calls ProcessAsync, exactly like MapTcpServer does - a regression in session lookup, map
// filtering, outcome application, or ordering inside that processor would fail these tests.
// FanOutAsync below reproduces ONLY MapTcpServer.RunMonsterTickLoopAsync's own fan-out loop (the
// part that calls NotifyMonsterMovedAsync/NotifyMonsterAttackOutcomeAsync per session per outcome)
// - never the orchestration/domain/damage logic itself, which stays entirely inside the real
// production types under test.
//
// Production call chain this exercises: MapTcpServer.RunMonsterTickLoopAsync ->
// MonsterEngagementTickProcessor.ProcessAsync -> MonsterEngagementDomain.Evaluate -> (Chase:
// MobInstance.AdvanceMovementForCombat/TryRetargetChase/TryStartChase/EnterChaseState) | (Attack:
// MobInstance.StopChase/EnterAttackState -> MapClientSession.TryGetCombatSnapshotAsync (re-checked
// immediately before commit) -> MobBasicAttackCalculator.Calculate -> MobInstance.
// ScheduleNextAttack -> MapClientSession.ApplyIncomingMobBasicAttackAsync (victim-only HP) ->
// MapTcpServer-style fan-out -> MapClientSession.NotifyMonsterAttackOutcomeAsync (area-visible
// 0x08C8) / NotifyMonsterMovedAsync (0x09FD walk-entry / 0x0088 fixpos)).
public sealed class MapClientSessionMobEngagementTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;
    private const uint OtherAccountId = 8;
    private const uint OtherCharId = 10;

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

    private static CharacterGameplayState FreshNovice(uint charId = CharId, ushort vit = 1, uint hp = 40) => new(
        CharacterId: charId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: hp, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: vit, Intelligence: 9, Dexterity: 9, Luck: 9);

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static byte[] BuildMovementRequest(ushort x, ushort y)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x035f);
        packet[2] = (byte)(x >> 2);
        packet[3] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        packet[4] = (byte)(y << 4);
        packet[5] = 0xab;
        return packet;
    }

    private static async Task ConsumeBootstrapAsync(Stream stream)
    {
        await ReadExact(stream, 4 + 6 + 6 + 13); // 0x0B18/0x0283/0x0ADE/0x02EB.
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa }); // 0x007D map-loaded.
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        // Unarmed with an empty inventory: neither the normal-item nor equip-item list packet is
        // sent at all (IroInventoryListPackets' own batch-count guards) - inventoryEnd follows
        // directly.
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd
    }

    // Consumes every 0x09FF monster-stand-entry packet a session's own visibility range produces
    // at connect time, so each test's own subsequent reads start from a clean wire regardless of
    // how many spawned mobs happen to be within range of that session's starting position.
    private static async Task ConsumeVisibleMonsterSpawnsAsync(Stream stream, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var header = await ReadExact(stream, 4);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            await ReadExact(stream, length - 4);
        }
    }

    private sealed record TestSession(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask);

    private async Task<TestSession> ConnectSessionAsync(
        MonsterRegistry registry, MonsterCombatCoordinator combat, uint accountId, uint charId,
        ushort x, ushort y, string map, CharacterGameplayState? gameplayState, int visibleMonsterCount,
        TimeProvider? timeProvider = null, IMapCollisionProvider? collisionProvider = null, IMovementPathProvider? movementPathProvider = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var gameplayPersistence = new RecordingGameplayStatePersistence(gameplayState ?? FreshNovice(charId));
        var session = new MapClientSession(
            (int)accountId, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            map, x, y, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: accountId, charId: charId, monsters: registry, combat: combat,
            timeProvider: timeProvider, collisionProvider: collisionProvider, movementPathProvider: movementPathProvider);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(accountId, charId, 1, 2, 0, 0, false, map, x, y, 0, 0, 0));

        await ConsumeBootstrapAsync(stream);
        await ConsumeVisibleMonsterSpawnsAsync(stream, visibleMonsterCount);

        return new TestSession(client, stream, session, run);
    }

    // Reproduces ONLY MapTcpServer.RunMonsterTickLoopAsync's own fan-out loop for a
    // MonsterEngagementTickResult - the production orchestration/domain/damage logic itself stays
    // entirely inside MonsterEngagementTickProcessor/MonsterEngagementDomain/
    // MobBasicAttackCalculator, never duplicated here.
    private static async Task FanOutAsync(MonsterEngagementTickResult result, IEnumerable<MapClientSession> sessions)
    {
        foreach (var session in sessions)
        {
            foreach (var change in result.MovementChanges) await session.NotifyMonsterMovedAsync(change, CancellationToken.None);
            foreach (var action in result.AttackActions) await session.NotifyMonsterAttackOutcomeAsync(action, CancellationToken.None);
        }
    }

    private sealed record TestWorld(MonsterRegistry Registry, MonsterCombatCoordinator Combat, MonsterEngagementTickProcessor Processor, MobInstance Poring, IMapCollisionProvider Collision, IMovementPathProvider PathProvider);

    // A real, fully-walkable MapCollisionMap-backed provider (not EmptyMapCollisionProvider, whose
    // own TryGetMap always returns false) - the Chase decision's fresh-walk fallback
    // (MonsterEngagementTickProcessor's own chase path) only ever calls ComputePath after its own
    // TryGetMap check succeeds, matching MonsterRuntimeTests' own real-collision-map fixture
    // pattern for the identical reason.
    private static TestWorld MakeWorld(ushort monsterX, ushort monsterY, string map = "int_land03", TimeProvider? timeProvider = null)
    {
        var allocator = new WorldActorIdAllocator();
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, map, 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator, new FixedCellSelector(monsterX, monsterY), TimeProvider.System);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules());
        var collisionMap = new MapCollisionMap(map, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray());
        IMapCollisionProvider collision = new MapCollisionProvider([collisionMap]);
        IMovementPathProvider pathProvider = new RathenaCompatibleMovementPathProvider(collision);
        var processor = new MonsterEngagementTickProcessor(registry, collision, pathProvider, timeProvider ?? TimeProvider.System);
        return new TestWorld(registry, combat, processor, registry.AllInstances[0], collision, pathProvider);
    }

    // Steps 1-9: passive Poring is attacked (acquires target), player is out of range, the
    // production processor runs, decision is Chase, the mob's authoritative position actually
    // moves toward the player, and the resulting outcome fans out a real 0x09FD walk-entry packet
    // to the observing session - the core observable fix for the reported "keeps running off"
    // behavior, all the way to the wire.
    [Fact]
    public async Task ProcessAsync_TargetOutOfRange_ChasesTowardThePlayer_AndFansOutMovement()
    {
        var world = MakeWorld(monsterX: 85, monsterY: 51);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", null, visibleMonsterCount: 1);
        using var _dispose = player.Client;

        world.Combat.Attack(world.Poring, AccountId, new(9, 9, 9, 9, 9, 9, 0, 0), 1, null, _ => CharacterQuestStatus.Absent);
        Assert.True(world.Poring.HasActiveTarget); // Attack itself acquires the target (real production path).

        var before = world.Poring.GetPosition();
        var result = await world.Processor.ProcessAsync([player.Session], CancellationToken.None);

        Assert.Equal(MobCombatState.Rush, world.Poring.Engagement.State);
        Assert.True(world.Poring.IsWalking, "Processor's Chase decision must actually start server-owned movement.");
        Assert.Single(result.MovementChanges);
        Assert.Equal(MonsterMovementChangeKind.WalkStarted, result.MovementChanges[0].Kind);

        await FanOutAsync(result, [player.Session]);
        var walkPacket = await ReadExact(player.Stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(walkPacket.AsSpan(2));
        await ReadExact(player.Stream, length - 4); // Drain the rest of the dynamic walk-entry packet.
        Assert.Equal((short)PacketConstants.ZcNotifyMoveEntry, BinaryPrimitives.ReadInt16LittleEndian(walkPacket));

        // Advance real elapsed time and confirm the mob is genuinely closer to the player, not
        // wandering to an unrelated destination.
        world.Poring.AdvanceMovement(DateTimeOffset.UtcNow.AddSeconds(2));
        var after = world.Poring.GetPosition();
        Assert.True(after.X < before.X, "Chase must move the mob toward the player's actual position.");

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Live-bug regression: the reported sequence was mob=(81,72), player moves away, chase begins,
    // then EVERY subsequent 100ms tick logged "retarget requested / retarget applied
    // previousCell=(81,72) reachedCell=(81,72)" forever - the mob never actually advanced past its
    // starting cell no matter how much real time passed. Root cause was
    // MobInstance.AdvanceMovementForCombat consuming/applying a pending retarget on EVERY call
    // regardless of whether AdvanceTo actually crossed a cell boundary this tick - since
    // ApplyChaseDecision re-requested the identical destination every tick (Evaluate re-derives
    // Chase(target's current cell) every tick by design), the retarget was perpetually re-applied
    // from the mob's still-unmoved current position, resetting the in-flight step's clock before it
    // could ever complete. The player is placed due east of the mob so the resulting chase path's
    // first (and only relevant) leg is a pure orthogonal step - WalkSpeed=400 (GeneratedMobs.
    // GPoring) makes that step exactly 400ms (CharacterMovementState.StepDurationMs's own
    // orthogonalStepMs-directly contract) - so ticking every 100ms, well short of a full step,
    // exactly reproduces the live 100ms engagement-tick cadence while keeping the step duration
    // deterministic regardless of the A* path provider's own diagonal-vs-orthogonal leg choice.
    [Fact]
    public async Task ProcessAsync_ChasingMobTickingEvery100Ms_AdvancesPastStartingCell_InsteadOfRepeatingIdenticalRetargetForever()
    {
        var clock = new ControllableTimeProvider();
        var world = MakeWorld(monsterX: 81, monsterY: 72, timeProvider: clock);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 90, 72, "int_land03", FreshNovice(hp: 40), visibleMonsterCount: 1, timeProvider: clock);
        using var _dispose = player.Client;

        world.Poring.TryAcquireTarget(AccountId, mode: MobMode.None);
        var startingCell = world.Poring.GetPosition();
        Assert.Equal((ushort)81, startingCell.X);
        Assert.Equal((ushort)72, startingCell.Y);

        // Tick 0 legitimately starts the fresh chase (a real WalkStarted, TryStartChase's own
        // fresh-walk branch) - the regression under test is what happens on every SUBSEQUENT tick
        // while that first step is still in flight.
        var firstResult = await world.Processor.ProcessAsync([player.Session], CancellationToken.None);
        Assert.Contains(firstResult.MovementChanges, c => c.Kind == MonsterMovementChangeKind.WalkStarted);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));

        for (var i = 0; i < 3; i++)
        {
            var result = await world.Processor.ProcessAsync([player.Session], CancellationToken.None);
            // None of these mid-step ticks may report a WalkStarted/retarget-applied movement
            // change - the mob is still genuinely mid-step, so nothing has actually changed yet.
            Assert.DoesNotContain(result.MovementChanges, c => c.Kind == MonsterMovementChangeKind.WalkStarted);
            var stillAtStart = world.Poring.GetPosition();
            Assert.Equal(startingCell.X, stillAtStart.X);
            Assert.Equal(startingCell.Y, stillAtStart.Y);
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        }

        // Enough real time has now elapsed (500ms total since the step started, past its 400ms
        // orthogonal duration) for the mob's current in-flight step to genuinely complete - the
        // very next tick must show real progress away from the starting cell, proving it was never
        // silently reset by any of the mid-step ticks above.
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        var finalResult = await world.Processor.ProcessAsync([player.Session], CancellationToken.None);
        var advanced = world.Poring.GetPosition();
        Assert.False(advanced.X == startingCell.X && advanced.Y == startingCell.Y, "The mob must have actually advanced past its starting cell once a real step boundary was reached.");
        Assert.Contains(finalResult.MovementChanges, c => c.Kind is MonsterMovementChangeKind.WalkStarted or MonsterMovementChangeKind.CellCrossed);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Live-bug regression, second half: even a STATIONARY far-away target must not cause a fresh
    // TryRetargetChase call (and therefore a fresh 0x09FD-shaped WalkStarted outcome) on every tick
    // once the mob is already genuinely chasing toward that exact destination - Evaluate correctly
    // re-derives an identical Chase(sameX, sameY) decision every tick (this is pinned behavior, not
    // a bug), but the ORCHESTRATOR must recognize "already chasing this exact destination" and do
    // nothing further until a real cell boundary or an actual destination change occurs.
    [Fact]
    public async Task ProcessAsync_StationaryFarAwayTarget_RepeatedTicksDoNotRepeatRetargetOrWalkStarted()
    {
        var clock = new ControllableTimeProvider();
        var world = MakeWorld(monsterX: 10, monsterY: 10, timeProvider: clock);
        // 20 cells away (diagonal), safely within RathenaCompatibleMovementPathProvider's own
        // pinned MAX_WALKPATH=32-step cap - far enough to stay out of AttackRange for many ticks,
        // but still within visibility range so the bootstrap actually sends the monster-spawn
        // packet ConsumeVisibleMonsterSpawnsAsync below expects.
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 20, 20, "int_land03", FreshNovice(hp: 40), visibleMonsterCount: 1, timeProvider: clock);
        using var _dispose = player.Client;

        world.Poring.TryAcquireTarget(AccountId, mode: MobMode.None);

        var firstResult = await world.Processor.ProcessAsync([player.Session], CancellationToken.None);
        Assert.Single(firstResult.MovementChanges);
        Assert.Equal(MonsterMovementChangeKind.WalkStarted, firstResult.MovementChanges[0].Kind);
        Assert.True(world.Poring.IsWalking);
        var destinationAfterFirstTick = world.Poring.MovementDestination;

        // Repeated ticks at the SAME (still far-away, still-not-moved) player position must produce
        // no further movement-change outcomes at all - no repeated retarget request, no repeated
        // WalkStarted - the mob simply continues its already-in-flight chase toward the same cell.
        for (var i = 0; i < 5; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
            var result = await world.Processor.ProcessAsync([player.Session], CancellationToken.None);
            Assert.DoesNotContain(result.MovementChanges, c => c.Kind == MonsterMovementChangeKind.WalkStarted);
        }

        Assert.Equal(destinationAfterFirstTick, world.Poring.MovementDestination);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Steps 10-14: drive the mob into range via the SAME processor, confirm the decision flips to
    // Attack, confirm real HP mutation + the correct stock-iRO packets arrive over the actual
    // client wire (via the production fan-out), confirm the attack cooldown withholds a second hit
    // before the pinned deadline, and confirm a later tick (once the deadline has passed) produces
    // the next hit.
    [Fact]
    public async Task ProcessAsync_TargetInRange_AttacksThroughRealSession_ThenRespectsCooldown_ThenHitsAgain()
    {
        var clock = new ControllableTimeProvider();
        var world = MakeWorld(monsterX: 76, monsterY: 51, timeProvider: clock);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(vit: 1, hp: 10_000), visibleMonsterCount: 1);
        using var _dispose = player.Client;

        // G_PORING's real Attack=1 cannot clear even a Vitality=1 target's def2 under the pinned RE
        // DEF-reduction formula (see MobBasicAttackCalculatorTests' own finding) - substitute a
        // stronger Attack purely so this test can observe real nonzero damage on the wire; the
        // ORCHESTRATION under test (processor -> domain -> calculator -> session -> fan-out) is
        // unaffected by which Attack value the mob_db row happens to carry.
        var strongMobDefinition = world.Poring.Spawn.Mob with { Attack = 1000, AttackDelay = 2000 };
        var strongRegistry = new MonsterRegistry([world.Poring.Spawn with { Mob = strongMobDefinition }], new WorldActorIdAllocator(), new FixedCellSelector(76, 51), TimeProvider.System);
        var strongProcessor = new MonsterEngagementTickProcessor(strongRegistry, world.Collision, world.PathProvider, clock);
        var strongMob = strongRegistry.AllInstances[0];
        strongMob.TryAcquireTarget(AccountId, mode: MobMode.None);

        var firstResult = await strongProcessor.ProcessAsync([player.Session], CancellationToken.None);
        Assert.Equal(MobCombatState.Berserk, strongMob.Engagement.State);
        Assert.Single(firstResult.AttackActions);
        await FanOutAsync(firstResult, [player.Session]);

        var damagePacket = await ReadExact(player.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        Assert.Equal(strongMob.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(2)));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(6)));
        var firstDamage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
        Assert.True(firstDamage > 0);
        // srcSpeed must be the mob's own AttackMotion (672 for G_PORING-shaped fixtures), never the
        // OTHER direction's DamageMotion (480) - section 11's own "do not reuse player->Poring
        // motion values for Poring->player" requirement.
        var wireSrcSpeed = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(14));
        Assert.Equal((uint)strongMobDefinition.AttackMotion, wireSrcSpeed);

        var hpPacket = await ReadExact(player.Stream, 8);
        Assert.Equal((short)PacketConstants.ZcParameterChange, BinaryPrimitives.ReadInt16LittleEndian(hpPacket));
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(hpPacket.AsSpan(2))); // SP_HP.
        var hpAfterFirstHit = BinaryPrimitives.ReadUInt32LittleEndian(hpPacket.AsSpan(4));
        Assert.Equal(10_000u - firstDamage, hpAfterFirstHit);
        Assert.Equal(hpAfterFirstHit, player.Session.GameplayState!.State.CurrentHp);

        // Cooldown: immediately re-running the processor before AttackDelay (2000ms) elapses must
        // produce no further hit at all.
        var cooldownResult = await strongProcessor.ProcessAsync([player.Session], CancellationToken.None);
        Assert.Empty(cooldownResult.AttackActions);
        Assert.Equal(hpAfterFirstHit, player.Session.GameplayState!.State.CurrentHp); // Unchanged.

        // Advance to (and past) the mob's own scheduled next-attack deadline, then run the SAME
        // processor again - the next hit must now occur.
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(2001));
        var secondResult = await strongProcessor.ProcessAsync([player.Session], CancellationToken.None);
        Assert.Single(secondResult.AttackActions);
        await FanOutAsync(secondResult, [player.Session]);

        var secondDamagePacket = await ReadExact(player.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(secondDamagePacket));
        var secondDamage = BinaryPrimitives.ReadUInt32LittleEndian(secondDamagePacket.AsSpan(22));
        Assert.True(secondDamage > 0);

        var secondHpPacket = await ReadExact(player.Stream, 8);
        var hpAfterSecondHit = BinaryPrimitives.ReadUInt32LittleEndian(secondHpPacket.AsSpan(4));
        Assert.Equal(hpAfterFirstHit - secondDamage, hpAfterSecondHit);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Section 7's own TOCTOU regression: the FIRST Evaluate (inside ProcessAsync) sees the player
    // in range and decides Attack, but the player moves out of range/dies BETWEEN that decision and
    // the actual hit-execution instant (simulated here by mutating the session's own gameplay state
    // directly, exactly like a concurrent packet-handler write would). The re-snapshot-then-
    // re-Evaluate inside TryApplyAttackAsync must catch this and produce ZERO remote damage/attack
    // outcome - never blindly trusting the earlier decision.
    [Fact]
    public async Task ProcessAsync_PlayerDiesBetweenEvaluateAndExecution_ProducesNoAttackOutcome()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(hp: 40), visibleMonsterCount: 1);
        using var _dispose = player.Client;
        world.Poring.TryAcquireTarget(AccountId, mode: MobMode.None);

        // Kill the player's gameplay state directly, simulating a concurrent event (e.g. another
        // mob's hit, or a status-effect tick) landing between this tick's Evaluate and its own
        // attack-execution instant - MutateAsync's own compare-and-swap means this is exactly the
        // kind of concurrent mutation TryApplyAttackAsync's re-snapshot must observe.
        await player.Session.GameplayState!.MutateAsync(s => s with { CurrentHp = 0 }, CancellationToken.None);

        var result = await world.Processor.ProcessAsync([player.Session], CancellationToken.None);

        Assert.Empty(result.AttackActions); // No attack outcome may be produced against a target that died in the interim.
        Assert.False(world.Poring.HasActiveTarget); // The fresh re-Evaluate inside execution unlocks, matching a genuinely dead target.

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Section 2/item G's own distinct scenario from the death case above: the player moves OUT OF
    // ATTACK RANGE (stays alive, same map) between the tick's own Evaluate and the actual
    // hit-execution instant - the re-Evaluate inside TryApplyAttackAsync must produce Chase, not
    // Unlock, and must never mutate HP or emit a successful attack outcome for this tick.
    //
    // Drives the state change through the SAME production seam MapTcpServer's own real tick would
    // observe a concurrent player movement through: a genuine 0x035F movement packet processed by
    // MapClientSession's own real packet-handling path, timed via
    // MonsterEngagementTickProcessor's own beforeFinalAttackRevalidation hook (a real orchestration
    // seam, not a test-only mutation method on MapClientSession) so the move completes exactly
    // between the tick's initial Evaluate and its later execution-time re-snapshot - no reflection,
    // no private-state mutation, no duplicated production algorithm.
    [Fact]
    public async Task ProcessAsync_PlayerMovesOutOfRangeBetweenEvaluateAndExecution_NoAttack_TransitionsToChase()
    {
        var clock = new ControllableTimeProvider();
        var world = MakeWorld(monsterX: 76, monsterY: 51, timeProvider: clock);
        var player = await ConnectSessionAsync(
            world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(hp: 40), visibleMonsterCount: 1,
            timeProvider: clock, collisionProvider: world.Collision, movementPathProvider: world.PathProvider);
        using var _dispose = player.Client;
        world.Poring.TryAcquireTarget(AccountId, mode: MobMode.None);

        var processor = new MonsterEngagementTickProcessor(world.Registry, world.Collision, world.PathProvider, clock, async () =>
        {
            // A real client movement request to a cell far enough away to leave AttackRange=1
            // once the single resulting step completes.
            await player.Stream.WriteAsync(BuildMovementRequest(90, 51));
            await ReadExact(player.Stream, 12); // 0x0087 movement-accepted response.
            await clock.AdvanceAsync(TimeSpan.FromSeconds(30)); // Real elapsed time for the walk to complete far past AttackRange.
            await player.Stream.WriteAsync(new byte[] { 0x1c, 0x0b }); // Synchronize on the movement having actually been processed.
            await ReadExact(player.Stream, 2);
        });

        var result = await processor.ProcessAsync([player.Session], CancellationToken.None);

        Assert.Empty(result.AttackActions); // No attack outcome may be produced against a target that moved out of range in the interim.
        Assert.Equal(40u, player.Session.GameplayState!.State.CurrentHp); // HP never mutated.
        Assert.True(world.Poring.HasActiveTarget); // Still engaged - just no longer in range.
        Assert.Equal(MobCombatState.Rush, world.Poring.Engagement.State); // Transitioned to Chase (Rush), not Unlock.

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Section 2/item D: a nearby SECOND observer (not the victim) also receives the 0x0088
    // combat-interruption fixpos when an engaged mob stops chasing to attack - movement outcomes
    // are AREA-visible exactly like the attack action itself (NotifyMonsterMovedAsync's own
    // existing per-session visibility gate, reused unchanged for ChaseInterrupted).
    [Fact]
    public async Task ProcessAsync_ChaseInterruptedToAttack_NearbySecondObserverAlsoReceivesFixpos()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51);
        var target = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(CharId, hp: 40), visibleMonsterCount: 1);
        using var _disposeTarget = target.Client;
        var bystander = await ConnectSessionAsync(world.Registry, world.Combat, OtherAccountId, OtherCharId, 80, 52, "int_land03", FreshNovice(OtherCharId, hp: 40), visibleMonsterCount: 1);
        using var _disposeBystander = bystander.Client;

        // Put the mob RIGHT NEXT TO the target (Chebyshev distance 1, within G_PORING's AttackRange
        // of 1) but still IsMoving (a long-duration in-flight step that has not completed yet) - so
        // this tick's Evaluate finds it already in range and decides Attack while a chase is still
        // technically in progress, reproducing "chase interrupted by an in-range decision". The path's
        // first cell must equal the mob's actual spawn cell (76,51) - TryStartChase never relocates
        // the mob to path[0]; a mismatched first cell is rejected (see MobInstanceTests' malformed-path
        // invariant regression).
        var stillWalkingPath = new (ushort X, ushort Y)[] { (76, 51), (76, 52) };
        Assert.True(world.Poring.TryStartChase(stillWalkingPath, orthogonalStepMs: 100_000, DateTimeOffset.UtcNow)); // Long duration - still walking when the tick runs.
        world.Poring.TryAcquireTarget(AccountId, mode: MobMode.None);
        world.Poring.EnterChaseState();
        Assert.True(world.Poring.IsWalking);

        var result = await world.Processor.ProcessAsync([target.Session, bystander.Session], CancellationToken.None);

        Assert.Contains(result.MovementChanges, c => c.Kind == MonsterMovementChangeKind.ChaseInterrupted);
        await FanOutAsync(result, [target.Session, bystander.Session]);

        // Both sessions must receive the 0x0088 fixpos - drain each stream's own next dynamic
        // packet header and confirm the opcode, since the exact packet ordering relative to the
        // subsequent attack action is already covered by other tests.
        await AssertReceivesFixposAsync(target.Stream);
        await AssertReceivesFixposAsync(bystander.Stream);

        target.Client.Close();
        bystander.Client.Close();
        await target.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
        await bystander.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task AssertReceivesFixposAsync(Stream stream)
    {
        var header = await ReadExact(stream, 8); // 0x0088 ZC_STOPMOVE: opcode.W + actorId.L + x.W + y.W = 8 bytes.
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(header));
    }

    // Section 10's own live-acceptance correction: REAL G_PORING (Attack=1) cannot clear a fresh
    // Novice's soft DEF under the pinned RENEWAL DEF-reduction formula, so this is the expected,
    // source-backed outcome for a real live acceptance run - the attack ACTION still fires (traced:
    // battle.cpp:7399's clif_damage runs unconditionally, miss or not), but with damage=0 and
    // IsMiss=true, and the resulting HP packet is correctly OMITTED (pc.cpp:9682-9687: "if (hp)
    // clif_updatestatus(...); else return;" - HP==0 never sends SP_HP). This is NOT a bug: it is
    // the real production chain (MonsterEngagementTickProcessor -> MobBasicAttackCalculator ->
    // MapClientSession) proving pinned zero-damage behavior end-to-end using the REAL generated mob
    // definition, not a strengthened test fixture.
    [Fact]
    public async Task ProcessAsync_RealGPoringAttack_MissesAFreshNovice_ActionSentButNoHpPacket()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(vit: 1, hp: 40), visibleMonsterCount: 1);
        using var _dispose = player.Client;
        world.Poring.TryAcquireTarget(AccountId, mode: MobMode.None);

        var result = await world.Processor.ProcessAsync([player.Session], CancellationToken.None);
        Assert.Single(result.AttackActions);
        var outcome = result.AttackActions[0];
        Assert.True(outcome.IsMiss);
        Assert.Equal(0u, outcome.Damage);
        Assert.False(outcome.HpChanged);

        await FanOutAsync(result, [player.Session]);

        // The action packet is still sent (damage=0, matching pinned clif_damage's own
        // unconditional call) - but NOTHING else follows it; confirm the wire goes quiet with a
        // bounded ping immediately after, proving no HP packet was sent. Every field asserted below
        // is exactly what pinned battle_calc_attack (battle.cpp:6753-6796) + clif_damage
        // (clif.cpp:5236, called from battle.cpp:7399 with wd.div_/wd.type) produce for a genuine
        // ATK_MISS on a plain (non-skill) basic attack - see MobBasicAttackCalculator's own doc
        // comment for the full trace of why this is a real miss, not an invented zero-damage hit.
        var damagePacket = await ReadExact(player.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        Assert.Equal(world.Poring.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(2)));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(6)));
        // srcSpeed/dstSpeed must still be the real nonzero AttackMotion/DamageMotion pair - a real
        // stock-iRO miss still plays the attacker's full swing animation timing, never a truncated
        // "nothing happened" no-op; only the damage number itself is 0.
        var wireSrcSpeed = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(14));
        var wireDstSpeed = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(18));
        Assert.Equal((uint)world.Poring.Spawn.Mob.AttackMotion, wireSrcSpeed);
        Assert.True(wireSrcSpeed > 0);
        Assert.True(wireDstSpeed > 0);
        var wireDamage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
        Assert.Equal(0u, wireDamage);
        var wireDiv = BinaryPrimitives.ReadUInt16LittleEndian(damagePacket.AsSpan(27));
        Assert.Equal(1, wireDiv); // wd.div_ = skill_id ? ... : 1 (battle.cpp:5286) - always 1 for a plain basic attack, hit or miss.
        var wireActionType = damagePacket[29];
        Assert.Equal(0, wireActionType); // DMG_NORMAL - dmg_lv's ATK_MISS reclassification is server-internal only, never a distinct wire `type`.

        await player.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(player.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));
        Assert.Equal(40u, player.Session.GameplayState!.State.CurrentHp); // Unchanged.

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Requirement 7 / item 9: target session missing entirely (e.g. process restarted mid-tick,
    // or was never found) resolves to the domain's own source-backed Unlock, applied by the real
    // processor - never a thrown exception or a silently-stuck engagement.
    [Fact]
    public async Task ProcessAsync_TargetSessionMissing_UnlocksTheMobThroughTheRealProcessor()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51);
        world.Poring.TryAcquireTarget(AccountId, mode: MobMode.None);

        await world.Processor.ProcessAsync([], CancellationToken.None); // No sessions at all.

        Assert.False(world.Poring.HasActiveTarget);
        Assert.Equal(MobCombatState.Idle, world.Poring.Engagement.State);
    }

    // Requirement 7: target session exists but has moved to a different map - the processor's own
    // snapshot correctly carries the CURRENT map, and the domain unlocks rather than attacking
    // across maps.
    [Fact]
    public async Task ProcessAsync_TargetOnADifferentMap_UnlocksThroughTheRealProcessor()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51, map: "int_land03");
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "iz_int03", null, visibleMonsterCount: 0);
        using var _dispose = player.Client;
        world.Poring.TryAcquireTarget(AccountId, mode: MobMode.None);

        await world.Processor.ProcessAsync([player.Session], CancellationToken.None);

        Assert.False(world.Poring.HasActiveTarget);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Requirement 7: target player is dead (CurrentHp == 0) - the processor's snapshot reflects
    // that, and the domain unlocks rather than continuing to attack a dead target.
    [Fact]
    public async Task ProcessAsync_TargetPlayerIsDead_UnlocksThroughTheRealProcessor()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(hp: 0), visibleMonsterCount: 1);
        using var _dispose = player.Client;
        world.Poring.TryAcquireTarget(AccountId, mode: MobMode.None);

        await world.Processor.ProcessAsync([player.Session], CancellationToken.None);

        Assert.False(world.Poring.HasActiveTarget);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Item 9 / section 12: a second, unrelated player near the same engaged mob must never be
    // damaged - the processor looks up the session by the mob's OWN TargetAccountId, never "any
    // nearby session". The bystander DOES see the area-visible attack ACTION (both are visible to
    // the same mob), but must NEVER receive the victim-only HP parameter update.
    [Fact]
    public async Task ProcessAsync_SecondUnrelatedSession_SeesActionButIsNeverDamaged()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51);
        var target = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(CharId, hp: 40), visibleMonsterCount: 1);
        using var _disposeTarget = target.Client;
        var bystander = await ConnectSessionAsync(world.Registry, world.Combat, OtherAccountId, OtherCharId, 76, 52, "int_land03", FreshNovice(OtherCharId, hp: 40), visibleMonsterCount: 1);
        using var _disposeBystander = bystander.Client;

        var strongMobSpawn = world.Poring.Spawn with { Mob = world.Poring.Spawn.Mob with { Attack = 1000 } };
        var strongRegistry = new MonsterRegistry([strongMobSpawn], new WorldActorIdAllocator(), new FixedCellSelector(76, 51), TimeProvider.System);
        var strongProcessor = new MonsterEngagementTickProcessor(strongRegistry, world.Collision, world.PathProvider, TimeProvider.System);
        var strongMob = strongRegistry.AllInstances[0];
        strongMob.TryAcquireTarget(AccountId, mode: MobMode.None);

        var result = await strongProcessor.ProcessAsync([target.Session, bystander.Session], CancellationToken.None);
        await FanOutAsync(result, [target.Session, bystander.Session]);

        // Both sessions see the AREA-visible action packet - bystander's own visibility already
        // covers this mob (both connected within its 14-cell discovery range).
        var targetDamagePacket = await ReadExact(target.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(targetDamagePacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(targetDamagePacket.AsSpan(6)));

        var bystanderActionPacket = await ReadExact(bystander.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(bystanderActionPacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(bystanderActionPacket.AsSpan(6))); // Still names the REAL victim as targetID.

        // Only the victim receives the self-only SP_HP update.
        var targetHpPacket = await ReadExact(target.Stream, 8);
        Assert.Equal((short)PacketConstants.ZcParameterChange, BinaryPrimitives.ReadInt16LittleEndian(targetHpPacket));

        // The bystander must receive NOTHING further (no HP packet) - prove the wire stays quiet
        // beyond the action with a bounded ping.
        await bystander.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var bystanderPingReply = await ReadExact(bystander.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(bystanderPingReply));
        Assert.Equal(40u, bystander.Session.GameplayState!.State.CurrentHp);

        target.Client.Close();
        bystander.Client.Close();
        await target.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
        await bystander.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Item 9: multiple mobs attacking the SAME player in one processor pass must serialize through
    // that player's own CharacterGameplayStateSession.MutateAsync gate - both hits land, HP
    // reflects both, never a lost update from concurrent mutation.
    [Fact]
    public async Task ProcessAsync_TwoMobsAttackingTheSamePlayer_BothHitsApplySerializedHpMutation()
    {
        var allocator = new WorldActorIdAllocator();
        var mobDefinition = GeneratedMobs.GPoring with { Attack = 1000 };
        var spawnA = new MobSpawnDefinition(mobDefinition, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var spawnB = new MobSpawnDefinition(mobDefinition, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 1));
        var registry = new MonsterRegistry([spawnA, spawnB], allocator, new FixedCellSelector(76, 51), TimeProvider.System);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules());
        var collisionMap = new MapCollisionMap("int_land03", 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray());
        IMapCollisionProvider collision = new MapCollisionProvider([collisionMap]);
        var processor = new MonsterEngagementTickProcessor(registry, collision, new RathenaCompatibleMovementPathProvider(collision), TimeProvider.System);
        var mobA = registry.AllInstances[0];
        var mobB = registry.AllInstances[1];

        // A large starting HP pool (not this class' usual 40) so BOTH hits land against a live
        // target and are individually observable - the point of this test is proving serialized
        // application of two hits within one tick, not proving lethal/overkill behavior (which
        // ApplyIncomingMobBasicAttackAsync's own "already dead" no-op branch already covers
        // elsewhere).
        var player = await ConnectSessionAsync(registry, combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(hp: 100_000), visibleMonsterCount: 2);
        using var _dispose = player.Client;

        mobA.TryAcquireTarget(AccountId, mode: MobMode.None);
        mobB.TryAcquireTarget(AccountId, mode: MobMode.None);

        var result = await processor.ProcessAsync([player.Session], CancellationToken.None);
        Assert.Equal(2, result.AttackActions.Count);
        await FanOutAsync(result, [player.Session]);

        var firstDamagePacket = await ReadExact(player.Stream, PacketConstants.ZcNotifyAct3Length);
        // Wire order per outcome is action-then-HP (see MonsterAttackActionOutcome's own doc
        // comment on why HP now follows its OWN action immediately, not batched after every
        // action): action1, hp1, action2, hp2 - not action1, action2, hp1, hp2.
        var firstHpPacket = await ReadExact(player.Stream, 8);
        var secondDamagePacket = await ReadExact(player.Stream, PacketConstants.ZcNotifyAct3Length);
        var secondHpPacket = await ReadExact(player.Stream, 8);

        var firstDamage = BinaryPrimitives.ReadUInt32LittleEndian(firstDamagePacket.AsSpan(22));
        var secondDamage = BinaryPrimitives.ReadUInt32LittleEndian(secondDamagePacket.AsSpan(22));
        var hpAfterFirst = BinaryPrimitives.ReadUInt32LittleEndian(firstHpPacket.AsSpan(4));
        var hpAfterSecond = BinaryPrimitives.ReadUInt32LittleEndian(secondHpPacket.AsSpan(4));

        Assert.Equal(100_000u - firstDamage, hpAfterFirst);
        Assert.Equal(hpAfterFirst - secondDamage, hpAfterSecond);
        Assert.Equal(hpAfterSecond, player.Session.GameplayState!.State.CurrentHp);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Item 18's own end-to-end scenario: a Poring already mid-walk on an idle random walk is
    // attacked, acquires the target, finishes its CURRENT in-flight cell, then at the next cell
    // boundary consumes the pending combat retarget and starts walking toward the player instead -
    // proving the exact live regression (section 3) is fixed through the real production chain.
    [Fact]
    public async Task ProcessAsync_MobAlreadyMidWalk_AcquiresTargetAndRetargetsAtTheNextCellBoundary()
    {
        var world = MakeWorld(monsterX: 90, monsterY: 51);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(hp: 10_000), visibleMonsterCount: 0); // Poring spawns at x=90, 15 cells away - outside the 14-cell visibility range at connect time.
        using var _dispose = player.Client;

        // Start the Poring on an idle walk AWAY from the player (toward x=99) - simulating "already
        // random-walking when attacked".
        var path = new (ushort X, ushort Y)[] { (90, 51), (91, 51), (92, 51), (93, 51) };
        Assert.True(world.Poring.TryStartChase(path, orthogonalStepMs: 400, DateTimeOffset.UtcNow));
        var beforeAttack = world.Poring.GetPosition();
        Assert.Equal((ushort)90, beforeAttack.X); // Still on the first cell - the in-flight step has not completed yet.

        // Attack lands while still mid-cell - acquires the target without disturbing the in-flight step.
        world.Combat.Attack(world.Poring, AccountId, new(9, 9, 9, 9, 9, 9, 0, 0), 1, null, _ => CharacterQuestStatus.Absent);
        Assert.True(world.Poring.HasActiveTarget);
        Assert.True(world.Poring.IsWalking, "The in-flight cell must not be interrupted merely by acquiring a target.");
        Assert.Equal(beforeAttack, world.Poring.GetPosition()); // No teleport - still exactly where it was.

        // Run the processor before the in-flight cell's own boundary - the retarget must remain
        // PENDING, not yet applied (the current cell must finish first).
        await world.Processor.ProcessAsync([player.Session], CancellationToken.None);
        Assert.Equal(beforeAttack, world.Poring.GetPosition());

        // Advance real time past the in-flight cell's own 400ms duration, then run the processor
        // again - THIS is where the pending retarget must be consumed and a fresh path toward the
        // player's CURRENT cell installed.
        await Task.Delay(450); // Real elapsed time - TimeProvider.System backs this world's processor.
        var retargetResult = await world.Processor.ProcessAsync([player.Session], CancellationToken.None);

        var reachedCell = world.Poring.GetPosition();
        Assert.Equal((ushort)91, reachedCell.X); // Finished the ORIGINAL in-flight cell first - never skipped/teleported.
        Assert.NotEmpty(retargetResult.MovementChanges); // The retarget application itself is a reported movement change.
        Assert.True(world.Poring.IsWalking, "The replacement path must now walk toward the player.");
        Assert.Equal(MobCombatState.Rush, world.Poring.Engagement.State);

        // Confirm the replacement path is now heading TOWARD the player (x decreasing from 91
        // toward 75), never continuing toward the original stale destination (93,51).
        await Task.Delay(1000);
        world.Poring.AdvanceMovement(DateTimeOffset.UtcNow);
        var afterRetarget = world.Poring.GetPosition();
        Assert.True(afterRetarget.X < reachedCell.X, "Poring must chase toward the player, not continue its stale idle-walk destination.");

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
