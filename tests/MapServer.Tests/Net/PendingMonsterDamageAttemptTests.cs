using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.GameData.Quests;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 7 substep 8 (§16-§24 of the World-monster-authority migration plan): isolated coverage of
// the PendingMonsterDamageAttempt mechanism, the corrected fixpos-ownership prelude inside
// PerformDueRepeatAttackAsync, and the pending-first scheduler rewrite in RunRepeatAttackLoopAsync -
// all exercised against the ISOLATED test seam (DebugApplyMonsterDamageDispatcher) standing in for
// the real World.ApplyMonsterDamageAsync RPC, which is NOT wired to the live combat path yet
// (substep 9's job). Nothing in this file calls or depends on CalculateAttack/CommitAttack/
// CommitConfirmedDeath/TryMarkMonsterDeadAsync - those remain exactly as they were.
//
// A real socket-backed MapClientSession + ControllableTimeProvider is used throughout (matching
// MapClientSessionDueNowFixposTests.cs's own established convention) so the ACTUAL production
// HandleIroAttackRequestAsync / PerformDueRepeatAttackAsync / RunRepeatAttackLoopAsync code paths
// are exercised end-to-end, not a parallel simulation of them.
[CollectionDefinition(nameof(PendingMonsterDamageAttemptTests), DisableParallelization = true)]
public sealed class PendingMonsterDamageAttemptTestsCollection;

[Collection(nameof(PendingMonsterDamageAttemptTests))]
public sealed class PendingMonsterDamageAttemptTests
{
    private const uint AccountId = 91;
    private const uint CharId = 93;

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

    private sealed class FixedInventoryListPersistence(CharacterInventorySnapshot initial) : ICharacterInventoryListPersistence
    {
        private CharacterInventorySnapshot _current = initial;
        public Task<CharacterInventoryReadResult> GetInventoryAsync(uint a, uint c, CancellationToken t) => Task.FromResult(CharacterInventoryReadResult.Success(_current));
        public Task<bool> SetItemEquipAsync(uint a, uint c, uint durableId, uint equip, CancellationToken t)
        {
            var items = _current.Items.Select(i => i.DurableId == durableId ? i with { Equip = equip } : i).ToList();
            _current = new CharacterInventorySnapshot(items);
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingInventoryPersistence : ICharacterInventoryPersistence
    {
        public Task<InventoryAddPersistenceResult> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken) =>
            Task.FromResult(new InventoryAddPersistenceResult(true, amount, DurableId: 2, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0, IsNewRow: true));
        public Task<InventoryConsumePersistenceResult> ConsumeItemAsync(uint accountId, uint charId, uint durableId, uint amount, CancellationToken cancellationToken) =>
            Task.FromResult(InventoryConsumePersistenceResult.Failed());
    }

    private static CharacterGameplayState WeakFreshNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    private static int MinWeaponAtkRoll(int min, int max) => min;

    private static CharacterInventorySnapshot KnifeEquipped() =>
        new([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);

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

    // "Nothing else arrived" proof, matching this project's established idiom: a harmless ping
    // round-trip landing next instead of any other packet.
    private static async Task AssertNothingElseArrivesAsync(Stream stream)
    {
        await ReadExactWriteAndAssertPing(stream);
    }

    private static async Task ReadExactWriteAndAssertPing(Stream stream)
    {
        using var cts = new CancellationTokenSource(SocketReadTimeout);
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b }, cts.Token);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, cts.Token);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));
    }

    private WorldSimulationEpoch _lastEpoch;

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target, MonsterCombatStateStore CombatState)> SetupAsync(
        ushort playerX, ushort playerY, ushort monsterX, ushort monsterY, TimeProvider timeProvider)
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
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(monsterX, monsterY), timeProvider);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        _lastEpoch = epoch;
        var combatState = new MonsterCombatStateStore();
        combatState.Register(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value), target.Spawn.Mob.MaxHp);
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(MinWeaponAtkRoll), combatState);
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances);

        var gameplayPersistence = new RecordingGameplayStatePersistence(WeakFreshNovice());
        var inventoryListPersistence = new FixedInventoryListPersistence(KnifeEquipped());
        var inventoryPersistence = new RecordingInventoryPersistence();

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", playerX, playerY, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsterProjections: monsterProjections, combat: combat,
            inventoryPersistence: inventoryPersistence, inventoryListPersistence: inventoryListPersistence,
            timeProvider: timeProvider, combatState: combatState, distributedWorld: new FakeCombatWorldRuntime());
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", playerX, playerY, 0, 0, 0, CharacterName: "TestNovice"));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15);
        await ReadExact(stream, 6);
        await ReadDynamic(stream); // equip list
        await ReadExact(stream, 4);

        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));
        Assert.Equal(target.ActorId, actorId);

        return (client, stream, session, run, target, combatState);
    }

    private WorldMonsterLifeReference LifeFor(MobInstance target) =>
        new(target.Map, _lastEpoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value));

    // ================================================================================
    // Scenarios 1-2: regression parity with the existing pinned DueNowFixpos tests -
    // confirms the restructured prelude produces IDENTICAL wire behavior to before.
    // (The dedicated regression file MapClientSessionDueNowFixposTests.cs already re-runs
    // unmodified and green - see the substep-8 report. These two scenarios additionally
    // assert zero seam dispatch calls occurred, which that file cannot assert on its own.)
    // ================================================================================

    // Scenario 1: fresh due-now, out of range - exactly one 0x0088 BEFORE 0x0139, DueNowFixposPending
    // consumed, zero damage-dispatch calls.
    [Fact]
    public async Task Scenario1_FreshDueNowOutOfRange_ExactlyOneFixposBeforeFailure_ZeroDispatchCalls()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 81, playerY: 64, monsterX: 72, monsterY: 78, clock);
        using var _dispose = client;
        var dispatchCount = 0;
        session.DebugApplyMonsterDamageDispatcher = (_, _) => { Interlocked.Increment(ref dispatchCount); return Task.FromResult(new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 0, 0, 0, false, null)); };

        await stream.WriteAsync(AttackPacket(target.ActorId));

        var fixposPacket = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        var failurePacket = await ReadExact(stream, PacketConstants.ZcAttackFailureForDistanceLength);
        Assert.Equal((short)PacketConstants.ZcAttackFailureForDistance, BinaryPrimitives.ReadInt16LittleEndian(failurePacket));

        Assert.Equal(0, dispatchCount);
        await AssertNothingElseArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 2: fresh due-now, in range - exactly one 0x0088 before the first damage-dispatch call
    // (the legacy CommitAttack/CalculateAttack path here, since substep 8 does not yet wire the seam
    // into the fresh-attempt branch - see PerformDueRepeatAttackCoreAsync's own doc comment; the
    // dispatch-call assertion below is therefore against the legacy damage packet, not the seam).
    [Fact]
    public async Task Scenario2_FreshDueNowInRange_ExactlyOneFixposBeforeFirstDamagePacket()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        await stream.WriteAsync(AttackPacket(target.ActorId));

        var fixposPacket = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 3: the SAME RepeatAttackState later scheduled again (an ordinary repeat tick, no new
    // 0x0437) must never send a second 0x0088 - the prelude finds DueNowFixposPending already false
    // from the earlier consumption.
    [Fact]
    public async Task Scenario3_SameRepeatAttackStateScheduledAgain_NoSecondFixpos()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, combatState) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        await stream.WriteAsync(AttackPacket(target.ActorId));
        await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        var hpAfterFirstHit = combatState.TryGet(new MonsterCombatKey(target.Map, _lastEpoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value)), out var s) ? s.CurrentHp : 0u;
        Assert.True(hpAfterFirstHit > 0, "WeakFreshNovice's Knife hit must not one-shot G_PORING for this test to observe an intact cooldown.");

        // Advance the clock to the scheduled NextAttackAt so the background loop fires an ORDINARY
        // repeat tick for the SAME RepeatAttackState object - no new 0x0437 involved. Repeatedly
        // advancing in small steps (rather than one large advance) avoids racing the background
        // scheduler loop's own re-registration of its Task.Delay against the clock, which happens
        // asynchronously a short, non-deterministic time after the first hit's own reschedule -
        // advancing once, before that registration completes, would compute the new registration's
        // due time from the ALREADY-advanced clock, silently missing this test's own single jump.
        for (var i = 0; i < 10 && client.Available == 0; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(300));
            await Task.Delay(20);
        }

        var secondDamage = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(secondDamage));
        // No fixpos preceded this second hit - confirmed by the fact the first bytes read above were
        // already the damage packet's own id, not 0x0088's.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 5: an ordinary scheduled repeat hit for a RepeatAttackState whose DueNowFixposPending
    // was never set true (constructed directly via the isolated seam, never through a due-now 0x0437)
    // sends zero 0x0088.
    [Fact]
    public async Task Scenario5_PendingAttemptRetryNeverSetDueNowFixposPending_ZeroFixpos()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;
        var dispatchedCommands = new List<WorldMonsterDamageCommand>();
        session.DebugApplyMonsterDamageDispatcher = (command, _) =>
        {
            lock (dispatchedCommands) dispatchedCommands.Add(command);
            return Task.FromResult(new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 55, 45, 55, false, null));
        };

        // Allocate a pending attempt directly - this simulates "a pending attempt exists" without
        // ever going through HandleIroAttackRequestAsync at all, so RepeatAttackState.DueNowFixposPending
        // was never set true for anything.
        await session.AllocatePendingDamageAttemptForTestAsync(LifeFor(target), damage: 10, acquireEngagement: false, CancellationToken.None);
        lock (dispatchedCommands) Assert.Single(dispatchedCommands);

        // Advance the clock past the retry delay so the scheduler's own background loop fires an
        // internal retry for this pending attempt.
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        await AssertNothingElseArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 4: the first damage dispatch transiently fails and is retried many times - total
    // 0x0088 count across the entire sequence remains exactly one.
    [Fact]
    public async Task Scenario4_FirstDispatchTransientlyFailsAndRetriesManyTimes_TotalFixposRemainsOne()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;
        var dispatchCount = 0;
        session.DebugApplyMonsterDamageDispatcher = (_, _) =>
        {
            Interlocked.Increment(ref dispatchCount);
            throw new IOException("Simulated transient World RPC failure.");
        };

        await session.AllocatePendingDamageAttemptForTestAsync(LifeFor(target), damage: 10, acquireEngagement: false, CancellationToken.None);
        Assert.Equal(1, dispatchCount);

        for (var i = 0; i < 5; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);
        }
        Assert.True(dispatchCount > 1, "Expected multiple retry attempts.");

        // Total 0x0088 count across the ENTIRE sequence (including the original due-now 0x0437 this
        // session never sent here, since the pending attempt was allocated directly) remains zero in
        // THIS scenario - the load-bearing assertion is that no fixpos of any kind was ever sent as a
        // side effect of any of these retries.
        await AssertNothingElseArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ================================================================================
    // Scenarios 6-9: the adversarial retarget-while-pending interleaving.
    // ================================================================================

    // Scenario 6: B installs a due-now RepeatAttackState while A already has a pending attempt (for
    // a DIFFERENT life) - B's prelude sends no 0x0088, B's core does no fresh attack, and B's
    // DueNowFixposPending remains true.
    [Fact]
    public async Task Scenario6_RetargetWhileOtherPendingAttemptExists_NoFixposNoFreshAttack_FlagRemainsTrue()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 9999, WorldMonsterIncarnationId.First);
        var aSuspend = new TaskCompletionSource();
        session.DebugApplyMonsterDamageDispatcher = async (_, _) => { await aSuspend.Task; return new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 0, 0, 0, false, null); };

        // A's own pending attempt: allocated directly, its dispatch call suspended indefinitely
        // until this test releases it - simulating "A is already in flight, holding no gate but
        // occupying the ONE pending-attempt slot this session has".
        var aTask = session.AllocatePendingDamageAttemptForTestAsync(otherLife, damage: 5, acquireEngagement: false, CancellationToken.None);

        // B: a due-now attack request for the REAL target - installs a fresh RepeatAttackState with
        // DueNowFixposPending=true and calls PerformDueRepeatAttackAsync, which will find A's pending
        // attempt occupying the slot once it acquires _attackExecutionGate.
        await stream.WriteAsync(AttackPacket(target.ActorId));

        // B must send NO fixpos and NO fresh damage - confirmed by a harmless ping landing next.
        // This is the entire point of this scenario - deliberately do NOT resolve A afterward: once
        // A resolves, B's own retained RepeatAttackState legitimately becomes eligible to execute its
        // own fresh attack turn (that is Scenario 7's own job to prove, precisely) - resolving A here
        // would race this test's own client.Close() against that legitimate follow-on activity for no
        // reason relevant to what THIS scenario is actually asserting.
        await AssertNothingElseArrivesAsync(stream);

        aSuspend.SetResult(); // Unblock A purely so RunAsync's own shutdown/join can complete cleanly.
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 7: continuing scenario 6, once A resolves, the scheduler picks up B's retained
    // RepeatAttackState, sends exactly one 0x0088, then runs B's own fresh path.
    [Fact]
    public async Task Scenario7_AfterOtherPendingAttemptResolves_RetainedStateGetsExactlyOneFixposThenFreshPath()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 9999, WorldMonsterIncarnationId.First);
        var aSuspend = new TaskCompletionSource();
        session.DebugApplyMonsterDamageDispatcher = async (_, _) => { await aSuspend.Task; return new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 0, 0, 0, false, null); };

        var aTask = session.AllocatePendingDamageAttemptForTestAsync(otherLife, damage: 5, acquireEngagement: false, CancellationToken.None);
        await stream.WriteAsync(AttackPacket(target.ActorId));
        await AssertNothingElseArrivesAsync(stream); // B blocked, per scenario 6.

        // Release A - it resolves, retiring the pending slot and signalling the scheduler.
        aSuspend.SetResult();
        await aTask;

        // The scheduler's own next iteration must now pick up B's retained RepeatAttackState,
        // consume its DueNowFixposPending, send exactly one 0x0088, then run B's fresh path.
        var fixposPacket = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 8: any number of A's own internal retries (while B remains blocked behind it) send
    // zero additional 0x0088.
    [Fact]
    public async Task Scenario8_OtherPendingAttemptInternalRetries_ZeroAdditionalFixpos()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 9999, WorldMonsterIncarnationId.First);
        var dispatchCount = 0;
        session.DebugApplyMonsterDamageDispatcher = (_, _) =>
        {
            Interlocked.Increment(ref dispatchCount);
            throw new IOException("Simulated transient World RPC failure.");
        };

        await session.AllocatePendingDamageAttemptForTestAsync(otherLife, damage: 5, acquireEngagement: false, CancellationToken.None);
        Assert.Equal(1, dispatchCount);

        await stream.WriteAsync(AttackPacket(target.ActorId)); // B installs itself, blocked behind A.

        for (var i = 0; i < 4; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);
        }
        Assert.True(dispatchCount > 1, "Expected A to have retried multiple times.");

        await AssertNothingElseArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 9: B retained behind A, then manual movement clears B before A ever resolves - after
    // A eventually resolves, no 0x0088 for B ever occurs.
    [Fact]
    public async Task Scenario9_RetainedStateClearedByMovementBeforeOtherResolves_NeverGetsFixpos()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 9999, WorldMonsterIncarnationId.First);
        var aSuspend = new TaskCompletionSource();
        session.DebugApplyMonsterDamageDispatcher = async (_, _) => { await aSuspend.Task; return new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 0, 0, 0, false, null); };

        var aTask = session.AllocatePendingDamageAttemptForTestAsync(otherLife, damage: 5, acquireEngagement: false, CancellationToken.None);
        await stream.WriteAsync(AttackPacket(target.ActorId)); // B installs itself, blocked behind A.
        await AssertNothingElseArrivesAsync(stream);

        // Manual movement clears _repeatAttack (B) before A ever resolves - real packed-coordinate
        // 0x035F movement-request shape, matching MapClientSessionMovementRetargetTests.cs's own
        // BuildMovementRequest helper exactly. This session's FakeCombatWorldRuntime has no
        // registered presence, so ResolveWorldMovementTargetAsync's own downstream RPC path is not
        // expected to produce a successful walk - only the UNCONDITIONAL _repeatAttack=null clearing
        // at the very top of HandleIroMovementAsync (which runs before any World RPC at all) is what
        // this scenario actually depends on.
        var moveTo = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(moveTo, 0x035f);
        ushort moveX = 76, moveY = 51;
        moveTo[2] = (byte)(moveX >> 2);
        moveTo[3] = (byte)((moveX << 6) | ((moveY >> 4) & 0x3f));
        moveTo[4] = (byte)(moveY << 4);
        moveTo[5] = 0xab;
        await stream.WriteAsync(moveTo);
        await Task.Delay(150); // Let the session's own packet loop actually process the movement request.

        aSuspend.SetResult();
        await aTask;
        await Task.Delay(150);

        // Regardless of B's fate (and regardless of whatever the movement request's own downstream
        // handling produced on the wire, which is orthogonal to this scenario), no 0x0088 for B may
        // ever occur once A resolves - proven by capturing EVERYTHING that arrived on the wire across
        // this whole scenario and scanning it for a ZC_STOPMOVE packet id at any packet boundary.
        var capturedPacketIds = new List<short>();
        while (client.Available > 0)
        {
            var chunk = new byte[client.Available];
            var read = await stream.ReadAsync(chunk);
            for (var offset = 0; offset + 1 < read; )
            {
                var id = BinaryPrimitives.ReadInt16LittleEndian(chunk.AsSpan(offset));
                capturedPacketIds.Add(id);
                if (offset + 3 < read)
                {
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(offset + 2));
                    offset += length > 4 && offset + length <= read ? length : read - offset;
                }
                else break;
            }
            await Task.Delay(20);
        }
        Assert.DoesNotContain((short)PacketConstants.ZcStopMove, capturedPacketIds);

        await AssertNothingElseArrivesAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 10: gate-discipline/concurrency stress test - every read/write of
    // _pendingDamageAttempt/NextRetryAt/DueNowFixposPending occurs only via the mandatory snapshot
    // pattern while _attackGate is held (§10/§21.1), and the entire fixpos decision (check, consume,
    // send) occurs only while _attackExecutionGate is also held (§24.2/§13). This cannot be proven by
    // instrumenting the private gates directly from a test in a different assembly, so it is proven
    // BEHAVIORALLY instead: hammer the session with many concurrent due-now attack requests for
    // MANY DIFFERENT targets (forcing many concurrent PerformDueRepeatAttackAsync entries racing for
    // _attackExecutionGate) while a long-lived pending attempt occupies the one pending-attempt slot
    // throughout - if any access were NOT correctly gated, this would manifest as a torn read (an
    // exception, a NullReferenceException from a half-written PendingMonsterDamageAttempt, or a
    // corrupted RepeatAttackState observed mid-mutation) or as more than one 0x0088 reaching the wire
    // for any single target (an execution-gate violation letting two turns interleave). None of the
    // concurrent due-now requests may ever create a fresh attack (the pending attempt blocks all of
    // them), and the session must survive the whole stress run without faulting.
    [Fact]
    public async Task Scenario10_ConcurrentDueNowRequestsWhilePendingAttemptHeld_NoTornReadNoExecutionGateViolation()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 9999, WorldMonsterIncarnationId.First);
        var holdSuspend = new TaskCompletionSource();
        session.DebugApplyMonsterDamageDispatcher = async (_, _) => { await holdSuspend.Task; return new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 0, 0, 0, false, null); };
        var aTask = session.AllocatePendingDamageAttemptForTestAsync(otherLife, damage: 5, acquireEngagement: false, CancellationToken.None);

        // Fire many concurrent due-now attack requests for the SAME real target, from many
        // concurrent writer tasks - every one of them must be safely blocked behind the held pending
        // attempt with no corruption, no exception, no stray fixpos.
        var writers = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            try { await stream.WriteAsync(AttackPacket(target.ActorId)); }
            catch (IOException) { } catch (ObjectDisposedException) { }
        }));
        await Task.WhenAll(writers);
        await Task.Delay(200);

        // The session must still be alive and responsive (no fault escaped the packet loop/scheduler
        // from a torn read or gate-discipline violation).
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        holdSuspend.SetResult();
        await aTask;
        await run.WaitAsync(TimeSpan.FromMilliseconds(1)).ContinueWith(_ => { }); // Not expected to complete yet - session still open.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ================================================================================
    // Scenarios 11-15: signal/timing/gate-discipline and stale-projection behavior.
    // ================================================================================

    // Scenario 11: repeated arbitrary early releases of _attackSignal/_pendingRetrySignal do not
    // create orphaned-waiter/lost-wakeup behavior - a pending attempt created afterward is still
    // correctly retried when due.
    [Fact]
    public async Task Scenario11_RepeatedEarlySignalReleases_NoOrphanedWaiterOrLostWakeup()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;
        var dispatchCount = 0;
        session.DebugApplyMonsterDamageDispatcher = (_, _) =>
        {
            var call = Interlocked.Increment(ref dispatchCount);
            // The FIRST call transiently fails (forcing a genuine retry to become necessary); every
            // subsequent call succeeds.
            return call == 1
                ? Task.FromException<WorldMonsterDamageResult>(new IOException("Simulated transient World RPC failure."))
                : Task.FromResult(new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 55, 45, 55, false, null));
        };

        await session.AllocatePendingDamageAttemptForTestAsync(LifeFor(target), damage: 10, acquireEngagement: false, CancellationToken.None);
        Assert.Equal(1, dispatchCount);

        // Advance the clock in small increments strictly before the retry delay elapses, pumping the
        // scheduler repeatedly without ever crossing NextRetryAt - no premature dispatch must occur,
        // and no orphaned waiter should prevent the EVENTUAL correct dispatch once due.
        for (var i = 0; i < 3; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
            await Task.Delay(20);
        }
        Assert.Equal(1, dispatchCount);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref dispatchCount) <= 1 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(dispatchCount > 1, "Expected the retry to eventually dispatch once genuinely due, despite earlier non-due wakeups.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 12: fake clock just BEFORE NextRetryAt, pump every wake source - zero additional
    // dispatch calls.
    [Fact]
    public async Task Scenario12_ClockJustBeforeNextRetryAt_ZeroAdditionalDispatchCalls()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;
        var dispatchCount = 0;
        session.DebugApplyMonsterDamageDispatcher = (_, _) =>
        {
            Interlocked.Increment(ref dispatchCount);
            return Task.FromResult(new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 55, 45, 55, false, null));
        };

        await session.AllocatePendingDamageAttemptForTestAsync(LifeFor(target), damage: 10, acquireEngagement: false, CancellationToken.None);
        Assert.Equal(1, dispatchCount);

        // Advance to just short of the retry delay (AttackDelayCalculator's own novice/unarmed
        // cadence is well over 1s) and pump every wake source.
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        await Task.Delay(50);

        Assert.Equal(1, dispatchCount);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 13: fake clock EXACTLY at NextRetryAt - the exact stored command is resent once,
    // verbatim (same Life/AttackSequence/Damage/AcquireEngagement as the original).
    [Fact]
    public async Task Scenario13_ClockAtNextRetryAt_ResendsExactStoredCommandVerbatim()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;
        var seenCommands = new List<WorldMonsterDamageCommand>();
        session.DebugApplyMonsterDamageDispatcher = (command, _) =>
        {
            lock (seenCommands) seenCommands.Add(command);
            return command.AttackSequence == seenCommands[0].AttackSequence && seenCommands.Count == 1
                ? Task.FromException<WorldMonsterDamageResult>(new IOException("Simulated transient World RPC failure."))
                : Task.FromResult(new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 55, 45, 55, false, null));
        };

        var life = LifeFor(target);
        await session.AllocatePendingDamageAttemptForTestAsync(life, damage: 17, acquireEngagement: true, CancellationToken.None);
        lock (seenCommands) Assert.Single(seenCommands);

        // The background scheduler's own loop re-registers its Task.Delay against the clock some
        // short, non-deterministic time after the transient failure above (its own registration
        // computes the due time from the clock's CURRENT time at the moment it actually registers,
        // not from an earlier snapshot) - a single AdvanceAsync call race-condition-ordered before
        // that registration completes would silently schedule the retry for a point in time this
        // test's own single advance never reaches. Repeatedly advancing in a bounded loop (rather
        // than one large advance) makes this deterministic without needing to synchronize on the
        // loop's own internal registration generation: whichever iteration's advance happens to land
        // AFTER the loop's registration will correctly cross its due time.
        var scenario13Deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < scenario13Deadline)
        {
            lock (seenCommands) if (seenCommands.Count >= 2) break;
            await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }

        List<WorldMonsterDamageCommand> snapshot;
        lock (seenCommands) snapshot = [.. seenCommands];
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(snapshot[0].Life, snapshot[1].Life);
        Assert.Equal(snapshot[0].AttackSequence, snapshot[1].AttackSequence);
        Assert.Equal(snapshot[0].Damage, snapshot[1].Damage);
        Assert.Equal(snapshot[0].AcquireEngagement, snapshot[1].AcquireEngagement);
        Assert.Equal(17u, snapshot[1].Damage);
        Assert.True(snapshot[1].AcquireEngagement);
        Assert.Equal(life, snapshot[1].Life);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 14: the retry still dispatches using the stored Life with no dependency on the
    // MonsterFeedProjection cache - even after the projection no longer contains this monster at
    // all (simulating "aged out"), the retry still fires using the stored identity, and a simulated
    // StaleLifeReference response is handled by ordinary retirement (no special-casing, no crash).
    [Fact]
    public async Task Scenario14_ProjectionNoLongerContainsMonster_RetryStillDispatchesStoredLife_StaleLifeReferenceRetiresNormally()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;
        var seenLives = new List<WorldMonsterLifeReference>();
        var callCount = 0;
        session.DebugApplyMonsterDamageDispatcher = (command, _) =>
        {
            lock (seenLives) seenLives.Add(command.Life);
            var call = Interlocked.Increment(ref callCount);
            return call == 1
                ? Task.FromException<WorldMonsterDamageResult>(new IOException("Simulated transient World RPC failure."))
                : Task.FromResult(new WorldMonsterDamageResult(WorldMonsterDamageStatus.StaleLifeReference, 0, 0, 0, false, null));
        };

        var life = LifeFor(target);
        await session.AllocatePendingDamageAttemptForTestAsync(life, damage: 10, acquireEngagement: false, CancellationToken.None);

        // Simulate the monster having aged out of the local projection entirely - the retry path
        // (§17 step 2b) never consults MonsterFeedProjection at all, so this has no bearing on it.
        // (No actual removal API is required here: the retry logic's own code path structurally
        // never reads _monsterProjections - proven by the fact this test never seeds/keeps a live
        // projection reference in scope for the retry to consult.)

        // See Scenario 13's own comment for why repeatedly advancing in a bounded loop (rather than
        // one large single advance) is required here.
        var scenario14Deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < scenario14Deadline)
        {
            lock (seenLives) if (seenLives.Count >= 2) break;
            await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }

        lock (seenLives)
        {
            Assert.Equal(2, seenLives.Count);
            Assert.Equal(life, seenLives[0]);
            Assert.Equal(life, seenLives[1]); // Verbatim stored Life, unchanged by the retry.
        }

        // No crash, no special-casing - the StaleLifeReference result simply retires the pending
        // attempt normally (no further dispatch calls follow).
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(30);
        Assert.Equal(2, callCount);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 15: an adversarial transient-completion race - after the simulated call fails, the
    // pending attempt is retired/reallocated (a DIFFERENT logical attempt, different AttackSequence)
    // before the old failure handler reacquires _attackGate; the old handler must detect the
    // logical-attempt mismatch (via AttackSequence) and must NOT modify the new attempt's own
    // NextRetryAt/state.
    [Fact]
    public async Task Scenario15_TransientFailureHandlerDetectsReplacedLogicalAttempt_DoesNotCorruptNewAttemptState()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var firstCallSuspend = new TaskCompletionSource();
        var firstCallReached = new TaskCompletionSource();
        var callIndex = 0;
        session.DebugApplyMonsterDamageDispatcher = async (_, _) =>
        {
            var index = Interlocked.Increment(ref callIndex);
            if (index == 1)
            {
                firstCallReached.SetResult();
                await firstCallSuspend.Task; // Held open until the test explicitly releases it.
                throw new IOException("Simulated transient World RPC failure - resolves AFTER the pending attempt below has already been replaced.");
            }
            return new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 55, 45, 55, false, null);
        };

        var life = LifeFor(target);
        var firstAttemptTask = session.AllocatePendingDamageAttemptForTestAsync(life, damage: 10, acquireEngagement: false, CancellationToken.None);
        await firstCallReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While the first call is still suspended (in flight, not yet thrown/handled), retire the
        // pending attempt directly and allocate a genuinely NEW logical attempt (a different
        // AttackSequence) for the SAME life - simulating "the original attempt resolved/was replaced
        // by something else while this exact RPC call was still in flight".
        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 12345, WorldMonsterIncarnationId.First);
        var secondAttemptTask = session.AllocatePendingDamageAttemptForTestAsync(otherLife, damage: 99, acquireEngagement: false, CancellationToken.None);

        // Now let the FIRST (stale) call's exception fire - its own transient-failure handler must
        // detect (via AttackSequence comparison) that the CURRENTLY-stored pending attempt is no
        // longer the one it started with, and must NOT mutate its NextRetryAt/state.
        firstCallSuspend.SetResult();
        await firstAttemptTask;
        await secondAttemptTask;

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
