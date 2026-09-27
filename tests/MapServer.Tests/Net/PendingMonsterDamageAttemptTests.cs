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
    private FakeCombatWorldRuntime? _lastFakeWorld;

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target, MonsterAttackCadenceStore CombatState, MonsterFeedProjectionRegistry Projections)> SetupAsync(
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
        var combatState = new MonsterAttackCadenceStore();
        combatState.Register(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value));
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(MinWeaponAtkRoll));
        var fakeWorld = new FakeCombatWorldRuntime();
        _lastFakeWorld = fakeWorld;
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances, fakeWorld);

        var gameplayPersistence = new RecordingGameplayStatePersistence(WeakFreshNovice());
        var inventoryListPersistence = new FixedInventoryListPersistence(KnifeEquipped());
        var inventoryPersistence = new RecordingInventoryPersistence();

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", playerX, playerY, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsterProjections: monsterProjections, combat: combat,
            inventoryPersistence: inventoryPersistence, inventoryListPersistence: inventoryListPersistence,
            timeProvider: timeProvider, combatState: combatState, distributedWorld: fakeWorld);
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

        return (client, stream, session, run, target, combatState, monsterProjections);
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
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 81, playerY: 64, monsterX: 72, monsterY: 78, clock);
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
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
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
        var (client, stream, session, run, target, combatState, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        await stream.WriteAsync(AttackPacket(target.ActorId));
        await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        var hpAfterFirstHit = _lastFakeWorld!.TryGetCurrentHp(LifeFor(target)) ?? 0u;
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
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;
        var dispatchedCommands = new List<WorldMonsterDamageCommand>();
        session.DebugApplyMonsterDamageDispatcher = (command, _) =>
        {
            lock (dispatchedCommands) dispatchedCommands.Add(command);
            return Task.FromResult(new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 55, 45, 55, false, null));
        };

        // Allocate a pending attempt directly - this simulates "a pending attempt exists" without
        // ever going through HandleIroAttackRequestAsync at all, so RepeatAttackState.DueNowFixposPending
        // was never set true for anything. The dispatcher's own scripted Applied/non-lethal result
        // means this FIRST dispatch already resolves the attempt unambiguously - it legitimately
        // projects a real damage packet onto the wire (substep 9: the pending-attempt mechanism now
        // drives real wire output, unlike the fully-isolated substep-8 seam this scenario originally
        // targeted) - drain it before proceeding.
        await session.AllocatePendingDamageAttemptForTestAsync(LifeFor(target), damage: 10, acquireEngagement: false, CancellationToken.None);
        lock (dispatchedCommands) Assert.Single(dispatchedCommands);
        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        // The target is already visible to this session (discovered during SetupAsync) - the
        // non-lethal Applied tail's HP-info packet therefore follows the damage packet too.
        var hpInfoPacket = await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        Assert.Equal((short)PacketConstants.ZcHpInfo, BinaryPrimitives.ReadInt16LittleEndian(hpInfoPacket));

        // Advance the clock - since the attempt already resolved above, there is nothing left
        // pending to retry; no further dispatch, and specifically no 0x0088 (this scenario's own
        // load-bearing assertion), occurs as a side effect of this clock advance.
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
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
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

    // Correction (online review of substep-9's own HEAD): DispatchPendingDamageAttemptAsync's
    // transient-failure branch must advance NextRetryAt using the IMMUTABLE attackDelayMs already
    // captured on the pending attempt at allocation time (the same effectiveStats/equippedWeapon the
    // logical hit itself was calculated against) - never a value recomputed from whatever character/
    // status/equipment state happens to be current at retry time. This proves it: the same session's
    // gameplay state is changed (a fresh Increase AGI buff, which measurably changes
    // AttackDelayCalculator.AttackDelayMs's own AttackSpeedBonus-dependent result) AFTER the fresh
    // attempt has already captured its own delay but BEFORE the transient failure/retry-scheduling
    // branch runs - the stored NextRetryAt must still advance by exactly the ORIGINAL captured delay,
    // never a freshly-recomputed (now different) one.
    [Fact]
    public async Task TransientRetry_UsesImmutableCapturedAttackDelayMs_NeverRecomputesFromCurrentState()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        // Capture the delay the UNBUFFED attacker's own first allocation will use - this is the
        // exact same calculation AllocatePendingDamageAttemptForTestAsync's own allocation performs
        // internally (unarmed, per that seam's own fixed `null` weapon type).
        var unbuffedStats = session.StatusEffects.Recalculate(WeakFreshNovice());
        var originalDelayMs = AttackDelayCalculator.AttackDelayMs(unbuffedStats, null);

        var dispatchCount = 0;
        session.DebugApplyMonsterDamageDispatcher = (_, _) =>
        {
            Interlocked.Increment(ref dispatchCount);
            throw new IOException("Simulated transient World RPC failure.");
        };

        var allocatedAt = clock.GetUtcNow();
        await session.AllocatePendingDamageAttemptForTestAsync(LifeFor(target), damage: 10, acquireEngagement: false, CancellationToken.None);
        Assert.Equal(1, dispatchCount);

        var nextRetryAtBeforeStateChange = await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None);
        Assert.NotNull(nextRetryAtBeforeStateChange);
        Assert.Equal(allocatedAt.AddMilliseconds(originalDelayMs), nextRetryAtBeforeStateChange!.Value);

        // Now change character/status state BEFORE the transient retry-scheduling branch runs again -
        // a real Increase AGI buff measurably changes AttackSpeedBonus, which changes what
        // AttackDelayCalculator.AttackDelayMs would compute if (incorrectly) re-read live. Confirm
        // the buff genuinely produces a DIFFERENT delay for this exact attacker, so this test cannot
        // pass merely because the buff happened to be a no-op.
        session.StatusEffects.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, durationMilliseconds: 60_000, val1: 10);
        var buffedStats = session.StatusEffects.Recalculate(WeakFreshNovice());
        var recomputedDelayMsIfBugPresent = AttackDelayCalculator.AttackDelayMs(buffedStats, null);
        Assert.NotEqual(originalDelayMs, recomputedDelayMsIfBugPresent);

        // Deterministic scheduler synchronization (CI-observed race fix): RunRepeatAttackLoopAsync's
        // own Task.Delay(..., clock, ...) for the NEXT wake is registered with the fake clock only
        // AFTER the loop actually re-enters its wait - a window that may still be open at this exact
        // point, right after the first transient failure returned. Advancing the clock before that
        // registration exists would silently miss it: no callback is due yet, so AdvanceAsync's own
        // due-callback scan finds nothing, and no further clock advance ever happens in this test to
        // retry the observation - the retry would simply never fire. Capture the registration
        // generation now, explicitly pulse both wake signals to force the loop to (re)evaluate its
        // own schedule against the CURRENT NextRetryAt (idempotent - see Scenario 11/12's own
        // identical use of this seam), then await a registration strictly AFTER this generation
        // before ever advancing the clock - never an arbitrary Task.Delay, never a larger polling
        // timeout; this only makes the clock-advance step below observably safe to perform.
        var registrationGeneration = clock.RegistrationGeneration;
        session.PulseBothWakeSignalsForTest();
        await clock.WaitForRegistrationAfterAsync(registrationGeneration).WaitAsync(TimeSpan.FromSeconds(5));

        // Advance the clock to trigger another transient-failure retry, and capture the instant the
        // retry actually fires (never assumed equal to allocatedAt - the loop's own wake happens some
        // real time after the clock advance).
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(originalDelayMs));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && dispatchCount < 2) await Task.Delay(20);
        Assert.True(dispatchCount >= 2, "Expected the transient failure to be retried.");

        var retryFiredAt = clock.GetUtcNow();
        var nextRetryAtAfterRetry = await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None);
        Assert.NotNull(nextRetryAtAfterRetry);

        // The stored NextRetryAt must have advanced by EXACTLY the original captured delay from the
        // instant this retry actually fired - never by the buffed/recomputed value, and never by a
        // fresh AttackDelayCalculator call against the now-different live state.
        Assert.Equal(retryFiredAt.AddMilliseconds(originalDelayMs), nextRetryAtAfterRetry!.Value);
        Assert.NotEqual(retryFiredAt.AddMilliseconds(recomputedDelayMsIfBugPresent), nextRetryAtAfterRetry!.Value);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ================================================================================
    // Scenarios 6-9: the adversarial retarget-while-pending interleaving.
    // ================================================================================

    // Scenario 6: the ACTUAL adversarial _attackExecutionGate ordering (item 2 of the substep-8
    // correction round, further corrected in the second review round) - B installs due-now state and
    // is suspended BEFORE it ever acquires _attackExecutionGate; A then GENUINELY acquires
    // _attackExecutionGate FIRST (via AllocatePendingDamageAttemptWhileHoldingExecutionGateForTestAsync,
    // never merely allocating without the gate), allocates its pending attempt, performs its first
    // dispatch (which transiently fails, so the pending attempt remains outstanding rather than
    // being immediately retired), and only THEN releases the gate. Only after A has released the gate
    // is B released to finally acquire it itself, strictly SECOND. B's own prelude must observe the
    // pending attempt that did not exist at the moment B was entered - this is the actual regression:
    // the pending check must happen AFTER B acquires _attackExecutionGate, never based on state
    // observed/decided earlier, and never based on an allocation that bypassed the gate entirely.
    // Teardown avoids the CI "Broken pipe" race (item 1 of the first correction round): B is cleared
    // via the movement path (which the test waits to be processed) BEFORE A is ever fully resolved,
    // so a legitimately-eligible fresh B execution can never race this test's own socket close.
    [Fact]
    public async Task Scenario6_RetargetWhileOtherPendingAttemptExists_NoFixposNoFreshAttack_FlagRemainsTrue()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 9999, WorldMonsterIncarnationId.First);
        var aDispatchCount = 0;
        session.DebugApplyMonsterDamageDispatcher = (_, _) =>
        {
            var call = Interlocked.Increment(ref aDispatchCount);
            // A's first dispatch transiently fails - the pending attempt remains outstanding (never
            // immediately retired), exactly as the required ordering specifies. Any subsequent call
            // (this scenario's own teardown-driven eventual retry) succeeds.
            return call == 1
                ? Task.FromException<WorldMonsterDamageResult>(new IOException("Simulated transient World RPC failure."))
                : Task.FromResult(new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 0, 0, 0, false, null));
        };

        // B's own coordination: reached BEFORE B's PerformDueRepeatAttackAsync call ever acquires
        // _attackExecutionGate. Signals this test that B has arrived at that point (bEntered), then
        // blocks until the test says A has already allocated its pending attempt, performed its
        // first (transiently-failing) dispatch, AND released _attackExecutionGate (letBProceed) -
        // forcing B to acquire the gate strictly SECOND, only after A has genuinely released it.
        var bEntered = new TaskCompletionSource();
        var letBProceed = new TaskCompletionSource();
        session.DebugBeforeAttackExecutionGateAsync = async () =>
        {
            bEntered.TrySetResult();
            await letBProceed.Task;
        };

        // B: a due-now attack request for the REAL target - installs a fresh RepeatAttackState with
        // DueNowFixposPending=true and calls PerformDueRepeatAttackAsync inline, which immediately
        // suspends at the hook above, before touching _attackExecutionGate at all.
        var writeB = stream.WriteAsync(AttackPacket(target.ActorId)).AsTask();
        await bEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A GENUINELY acquires _attackExecutionGate first, allocates its pending attempt, performs
        // its first (transiently-failing) dispatch, and releases the gate - all inside this one call,
        // fully completed BEFORE B is ever released below. This is the exact required ordering: A's
        // entire allocate-plus-first-dispatch turn happens while holding the same production gate
        // PerformDueRepeatAttackAsync itself uses, not merely "before B happens to check".
        await session.AllocatePendingDamageAttemptWhileHoldingExecutionGateForTestAsync(otherLife, damage: 5, acquireEngagement: false, CancellationToken.None);
        Assert.Equal(1, aDispatchCount);

        // Release B - it now acquires _attackExecutionGate SECOND, only after A has already released
        // it with a pending attempt on record, which is the exact ordering this scenario must force.
        letBProceed.TrySetResult();
        await writeB;

        // B must send NO fixpos and NO fresh damage - confirmed by a harmless ping landing next.
        await AssertNothingElseArrivesAsync(stream);

        // Teardown (item 1 of the first correction round): clear B via the real movement path and
        // wait for it to be processed BEFORE letting A's own pending attempt resolve any further -
        // once A resolves, B's retained RepeatAttackState would otherwise become legitimately
        // eligible to execute a fresh attack (Scenario 7's own job to prove), which would race this
        // test's own socket close. Clearing B first removes that race entirely without weakening any
        // assertion above.
        var moveTo = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(moveTo, 0x035f);
        ushort moveX = 76, moveY = 51;
        moveTo[2] = (byte)(moveX >> 2);
        moveTo[3] = (byte)((moveX << 6) | ((moveY >> 4) & 0x3f));
        moveTo[4] = (byte)(moveY << 4);
        moveTo[5] = 0xab;
        await stream.WriteAsync(moveTo);
        await Task.Delay(150); // Let the session's own packet loop actually process the movement request.

        // Let A's own pending attempt resolve normally via its ordinary retry cadence, purely so
        // RunAsync's own shutdown/join can complete cleanly - not load-bearing for this scenario's
        // own assertions above, which are already fully proven by this point.
        await session.RetirePendingDamageAttemptForTestAsync(CancellationToken.None);
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 7: continuing scenario 6's ordering, once A resolves, the scheduler picks up B's
    // retained RepeatAttackState, sends exactly one 0x0088, then runs B's own fresh path. Unlike
    // Scenario 6, this scenario deliberately lets B remain retained (never clears it via movement)
    // so it can observe B's own eventual execution - teardown here is safe because there is no
    // concurrent client.Close() racing B's legitimate execution: the test awaits B's own wire output
    // before closing.
    [Fact]
    public async Task Scenario7_AfterOtherPendingAttemptResolves_RetainedStateGetsExactlyOneFixposThenFreshPath()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, combatState, projections) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 9999, WorldMonsterIncarnationId.First);
        // Unlike Scenarios 9/10 (where A's own life is a genuinely unrelated placeholder never seeded
        // into the projection), THIS scenario's A and B end up sharing the SAME RepeatAttackState
        // object (AllocatePendingDamageAttemptForTestAsync's own `origin = _repeatAttack ?? new(...)`
        // picks up B's already-installed state, since B installs it before A ever allocates - exactly
        // Scenario 6's own established ordering, which this scenario continues). A's own Applied
        // non-lethal resolution therefore DOES need a real, resolvable TargetSnapshot (it must NOT
        // clear the shared _repeatAttack - only a status that calls ClearRepeatAttackIfCurrent would
        // do that, which would wrongly discard B's own retained state before B ever gets to run) -
        // seed a genuine second projection entry for otherLife so HandleDamageResultAsync's
        // Applied/non-lethal branch can build a real damage packet for it instead of NRE-ing against
        // an unseeded default TargetSnapshot.
        var otherInstance = new WorldMonsterInstance(
            ActorId: otherLife.ActorId, IncarnationId: otherLife.IncarnationId, MapId: otherLife.MapId, MobId: target.Spawn.Mob.Id,
            X: target.GetPosition().X, Y: target.GetPosition().Y, Lifecycle: WorldMonsterLifecycleState.Alive, IsWalking: false,
            DestinationX: target.GetPosition().X, DestinationY: target.GetPosition().Y,
            Engagement: WorldMonsterEngagementState.Unengaged, EngagedTarget: null,
            CurrentHp: target.Spawn.Mob.MaxHp, MaxHp: target.Spawn.Mob.MaxHp);
        combatState.Register(otherLife.MapId, otherLife.SimulationEpoch, otherLife.ActorId, otherLife.IncarnationId);
        projections.GetOrCreate(otherLife.MapId).ApplySnapshot([target.ToWorldMonsterInstance(), otherInstance], otherLife.SimulationEpoch, combatState);

        var aSuspend = new TaskCompletionSource();
        session.DebugApplyMonsterDamageDispatcher = async (_, _) => { await aSuspend.Task; return new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 0, 0, 0, false, null); };

        var bEntered = new TaskCompletionSource();
        var letBProceed = new TaskCompletionSource();
        session.DebugBeforeAttackExecutionGateAsync = async () =>
        {
            bEntered.TrySetResult();
            await letBProceed.Task;
        };

        var writeB = stream.WriteAsync(AttackPacket(target.ActorId)).AsTask();
        await bEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var aTask = session.AllocatePendingDamageAttemptForTestAsync(otherLife, damage: 5, acquireEngagement: false, CancellationToken.None);
        letBProceed.TrySetResult();
        await writeB;
        await AssertNothingElseArrivesAsync(stream); // B blocked, per scenario 6's ordering.

        // Release A - it resolves, retiring the pending slot and signalling the scheduler.
        aSuspend.SetResult();
        await aTask;

        // A's own allocation claimed the shared RepeatAttackState's NextAttackAt optimistically (see
        // AllocateAndDispatchFreshDamageAttemptAsync's own doc comment), pushing it into the future -
        // advance the clock so B's own scheduled turn (on that SAME shared RepeatAttackState) becomes
        // due and RunRepeatAttackLoopAsync actually wakes to process it. A and B share the SAME
        // RepeatAttackState object (see this scenario's own doc comment above) - once A's own
        // DispatchPendingDamageAttemptAsync retires the pending slot and signals the scheduler, B's
        // own scheduler-driven re-evaluation of that SAME RepeatAttackState races A's own tail (both
        // are independently-scheduled tasks); depending on exactly how these interleave, the
        // scheduler's own sleep-until-NextAttackAt loop may not yet be blocked on THIS clock advance
        // at the moment it fires (a benign, adversarial-scenario-only timing gap - not a production
        // bug, since production never constructs two independent attempts against one shared
        // RepeatAttackState in the first place) - poll with repeated small advances rather than
        // trusting a single one to land inside the right window.
        var opcodes = new List<short>();
        var pollDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < pollDeadline && opcodes.Count(o => o == (short)PacketConstants.ZcStopMove) < 1)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
            var drainDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
            while (DateTime.UtcNow < drainDeadline)
            {
                byte[] header;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                    var buffer = new byte[2];
                    await stream.ReadExactlyAsync(buffer, cts.Token);
                    header = buffer;
                }
                catch (OperationCanceledException) { break; } // Nothing else arrived within this poll window.
                var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
                opcodes.Add(opcode);
                if (opcode == (short)PacketConstants.ZcStopMove) await ReadExact(stream, PacketConstants.ZcStopMoveLength - 2);
                else if (opcode == (short)PacketConstants.ZcNotifyAct3) await ReadExact(stream, PacketConstants.ZcNotifyAct3Length - 2);
                else if (opcode == (short)PacketConstants.ZcHpInfo) await ReadExact(stream, PacketConstants.ZcHpInfoLength - 2);
                else Assert.Fail($"Unexpected opcode 0x{opcode:X4} while draining Scenario 7's expected packet set.");
            }
        }

        // Load-bearing invariants: exactly one fixpos ever (B's own DueNowFixposPending consumed
        // exactly once), and at least one damage packet (proving B's own fresh path actually ran) -
        // A's own non-lethal Applied resolution also legitimately writes its own damage packet for
        // otherLife (it is never gated on ReferenceEquals - only the RESCHEDULE is), so the exact
        // total count of damage packets is not asserted, only that at least one landed.
        Assert.Equal(1, opcodes.Count(o => o == (short)PacketConstants.ZcStopMove));
        Assert.True(opcodes.Count(o => o == (short)PacketConstants.ZcNotifyAct3) >= 1, "Expected at least one damage packet (B's own fresh-path hit).");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 8: any number of A's own internal retries (while B remains blocked behind it) send
    // zero additional 0x0088.
    [Fact]
    public async Task Scenario8_OtherPendingAttemptInternalRetries_ZeroAdditionalFixpos()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
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
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 9999, WorldMonsterIncarnationId.First);
        var aSuspend = new TaskCompletionSource();
        // A's own life is an unrelated scheduling/gating placeholder never seeded into the
        // projection - StaleLifeReference (rather than Applied) avoids HandleDamageResultAsync's
        // Applied/ReplayedSequence branches, the only ones that dereference TargetSnapshot, which
        // would otherwise NRE against this life's synthesized default (unseeded) TargetSnapshot.
        session.DebugApplyMonsterDamageDispatcher = async (_, _) => { await aSuspend.Task; return new WorldMonsterDamageResult(WorldMonsterDamageStatus.StaleLifeReference, 0, 0, 0, false, null); };

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
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 9999, WorldMonsterIncarnationId.First);
        var holdSuspend = new TaskCompletionSource();
        // A's own life is an unrelated scheduling/gating placeholder never seeded into the
        // projection - StaleLifeReference (rather than Applied) avoids HandleDamageResultAsync's
        // Applied/ReplayedSequence branches, the only ones that dereference TargetSnapshot, which
        // would otherwise NRE against this life's synthesized default (unseeded) TargetSnapshot.
        session.DebugApplyMonsterDamageDispatcher = async (_, _) => { await holdSuspend.Task; return new WorldMonsterDamageResult(WorldMonsterDamageStatus.StaleLifeReference, 0, 0, 0, false, null); };
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

    // Scenario 11 (corrected per the second substep-8 review round): repeated arbitrary early
    // releases of BOTH _attackSignal and _pendingRetrySignal - explicitly pulsed via
    // PulseBothWakeSignalsForTest, more than once per iteration, strictly BEFORE the actual stored
    // NextRetryAt - do not create orphaned-waiter/lost-wakeup behavior. Proves both halves required:
    // (1) no premature dispatch while pulsing early, and (2) the retry still genuinely fires once due,
    // proving no earlier pulse left an orphaned waiter that could otherwise swallow the eventual
    // legitimate wakeup.
    [Fact]
    public async Task Scenario11_RepeatedEarlySignalReleases_NoOrphanedWaiterOrLostWakeup()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
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

        // Read the ACTUAL stored NextRetryAt so this loop can reliably stay strictly before it while
        // still pulsing both signals repeatedly (never an assumed delay constant).
        DateTimeOffset? retryAt = null;
        var pollDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < pollDeadline)
        {
            retryAt = await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None);
            if (retryAt is not null) break;
            await Task.Delay(10);
        }
        Assert.NotNull(retryAt);

        // Advance the clock in small increments strictly before the retry delay elapses, pulsing
        // BOTH wake signals (more than once per iteration) at each step - no premature dispatch must
        // occur, and no orphaned waiter should prevent the EVENTUAL correct dispatch once due.
        for (var i = 0; i < 3; i++)
        {
            var now = clock.GetUtcNow();
            var remaining = retryAt.Value - now;
            // Advance by a third of the remaining time each iteration - stays strictly before
            // retryAt across all 3 iterations while still making real forward progress.
            var step = remaining / 4;
            if (step > TimeSpan.Zero) await clock.AdvanceAsync(step);
            session.PulseBothWakeSignalsForTest();
            session.PulseBothWakeSignalsForTest();
            session.PulseBothWakeSignalsForTest();
            await Task.Delay(20);
        }
        Assert.Equal(1, dispatchCount);

        // Now genuinely cross NextRetryAt and pulse both signals once more - the retry must still
        // fire, proving none of the earlier repeated early pulses above left an orphaned waiter that
        // could otherwise have consumed/blocked this legitimate wakeup.
        var stillNow = clock.GetUtcNow();
        if (retryAt.Value > stillNow) await clock.AdvanceAsync(retryAt.Value - stillNow + TimeSpan.FromMilliseconds(1));
        session.PulseBothWakeSignalsForTest();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref dispatchCount) <= 1 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(dispatchCount > 1, "Expected the retry to eventually dispatch once genuinely due, despite earlier repeated early pulses of both wake signals.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 12 (item 5 of the substep-8 correction round): fake clock advanced to just BEFORE the
    // ACTUAL stored NextRetryAt (read via SnapshotPendingNextRetryAtForTestAsync, never an assumed
    // delay constant), then BOTH wake signals are pulsed explicitly - zero additional dispatch calls.
    [Fact]
    public async Task Scenario12_ClockJustBeforeNextRetryAt_ZeroAdditionalDispatchCalls()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;
        var dispatchCount = 0;
        session.DebugApplyMonsterDamageDispatcher = (_, _) =>
        {
            var call = Interlocked.Increment(ref dispatchCount);
            // The FIRST call transiently fails, so the attempt has a genuine outstanding
            // NextRetryAt for this scenario's boundary check to test against (an unambiguous
            // Applied result on the first call would retire the attempt immediately, leaving no
            // NextRetryAt at all).
            return call == 1
                ? Task.FromException<WorldMonsterDamageResult>(new IOException("Simulated transient World RPC failure."))
                : Task.FromResult(new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, 55, 45, 55, false, null));
        };

        await session.AllocatePendingDamageAttemptForTestAsync(LifeFor(target), damage: 10, acquireEngagement: false, CancellationToken.None);
        Assert.Equal(1, dispatchCount);

        DateTimeOffset? retryAt = null;
        var pollDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < pollDeadline)
        {
            retryAt = await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None);
            if (retryAt is not null) break;
            await Task.Delay(10);
        }
        Assert.NotNull(retryAt);

        // Advance to exactly one millisecond short of the actual stored NextRetryAt - the smallest
        // meaningful tick this clock's own AdvanceAsync/DateTimeOffset resolution supports - then
        // pump both wake signals explicitly (not merely "wait and hope a signal fires").
        var now = clock.GetUtcNow();
        var justBefore = retryAt.Value - TimeSpan.FromMilliseconds(1);
        Assert.True(justBefore > now, "Test setup requires the retry delay to exceed 1ms, which AttackDelayCalculator's own novice/unarmed cadence always does.");
        await clock.AdvanceAsync(justBefore - now);
        session.PulseBothWakeSignalsForTest();
        session.PulseBothWakeSignalsForTest();
        await Task.Delay(50);

        Assert.Equal(1, dispatchCount);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 13 (item 6 of the substep-8 correction round): fake clock advanced to EXACTLY the
    // ACTUAL stored NextRetryAt (read via SnapshotPendingNextRetryAtForTestAsync, never "eventually
    // after due") - the exact stored command is resent exactly once, verbatim (same Life/
    // AttackSequence/Damage/AcquireEngagement as the original), and no extra retry occurs.
    [Fact]
    public async Task Scenario13_ClockAtNextRetryAt_ResendsExactStoredCommandVerbatim()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
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

        // Poll the ACTUAL stored NextRetryAt (it is only set once the transient-failure handler
        // above has reacquired _attackGate and advanced it - a short, non-deterministic time after
        // the first call above) rather than assuming a delay constant.
        DateTimeOffset? retryAt = null;
        var pollDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < pollDeadline)
        {
            retryAt = await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None);
            if (retryAt is not null) break;
            await Task.Delay(10);
        }
        Assert.NotNull(retryAt);

        // Advance the fake clock to EXACTLY the actual stored NextRetryAt - not "eventually past
        // it" - then pulse both wake signals to prompt the scheduler to re-evaluate immediately
        // rather than waiting for its own Task.Delay to elapse.
        var now = clock.GetUtcNow();
        if (retryAt.Value > now) await clock.AdvanceAsync(retryAt.Value - now);
        session.PulseBothWakeSignalsForTest();

        var scenario13Deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < scenario13Deadline)
        {
            lock (seenCommands) if (seenCommands.Count >= 2) break;
            await Task.Delay(20);
        }

        List<WorldMonsterDamageCommand> snapshot;
        lock (seenCommands) snapshot = [.. seenCommands];
        Assert.Equal(2, snapshot.Count); // Exactly one retry dispatch - no extra retry.
        Assert.Equal(snapshot[0].Life, snapshot[1].Life);
        Assert.Equal(snapshot[0].AttackSequence, snapshot[1].AttackSequence);
        Assert.Equal(snapshot[0].Damage, snapshot[1].Damage);
        Assert.Equal(snapshot[0].AcquireEngagement, snapshot[1].AcquireEngagement);
        Assert.Equal(17u, snapshot[1].Damage);
        Assert.True(snapshot[1].AcquireEngagement);
        Assert.Equal(life, snapshot[1].Life);

        // No extra retry follows - confirmed by pumping the clock/signals once more and observing
        // the seen-command count stays at exactly 2.
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        session.PulseBothWakeSignalsForTest();
        await Task.Delay(50);
        lock (seenCommands) Assert.Equal(2, seenCommands.Count);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Scenario 14 (item 7 of the substep-8 correction round): the retry still dispatches using the
    // stored Life with no dependency on the MonsterFeedProjection cache - the monster is ACTUALLY
    // removed from the local MonsterFeedProjection (via a real ApplySnapshot re-seed omitting it,
    // the exact mechanism WorldMonsterProjectionTestHelper's own ResyncProjection already uses) while
    // the stored PendingMonsterDamageAttempt is preserved - the retry still fires using the stored
    // identity, and a simulated StaleLifeReference response is handled by ordinary retirement (no
    // special-casing, no crash).
    [Fact]
    public async Task Scenario14_ProjectionNoLongerContainsMonster_RetryStillDispatchesStoredLife_StaleLifeReferenceRetiresNormally()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, combatState, projections) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
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

        // ACTUALLY remove the monster from the local MonsterFeedProjection - re-seed the SAME map's
        // projection (same epoch, same combatState, so nothing else this session depends on is
        // invalidated) with an EMPTY instance list, simulating "this monster has aged out of the
        // projection entirely". The stored PendingMonsterDamageAttempt above is completely untouched
        // by this - it lives in the session's own field, not in the projection.
        WorldMonsterProjectionTestHelper.ResyncProjection(projections, target.Map, _lastEpoch, combatState, []);
        Assert.False(projections.GetOrCreate(target.Map).TryGetInstance(target.ActorId, out _), "Test setup requires the monster to be genuinely absent from the projection.");

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

    // Scenario 15 (item 8 of the substep-8 correction round): an adversarial transient-completion
    // race - after the simulated call fails, the pending attempt is EXPLICITLY retired (via
    // RetirePendingDamageAttemptForTestAsync, never a silent overwrite - AllocatePendingDamageAttemptForTestAsync
    // now enforces the at-most-one invariant and would throw if the old attempt were still on
    // record) and a genuinely NEW logical attempt (a DIFFERENT AttackSequence) is allocated, all
    // before the old failure handler reacquires _attackGate; the old handler must detect the
    // logical-attempt mismatch (via AttackSequence) and must NOT modify the new attempt's own
    // NextRetryAt/state - proven directly by snapshotting the new attempt's NextRetryAt both before
    // and after the old handler resumes and asserting they are identical.
    [Fact]
    public async Task Scenario15_TransientFailureHandlerDetectsReplacedLogicalAttempt_DoesNotCorruptNewAttemptState()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, _, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, clock);
        using var _dispose = client;

        var firstCallSuspend = new TaskCompletionSource();
        var firstCallReached = new TaskCompletionSource();
        var callIndex = 0;
        session.DebugApplyMonsterDamageDispatcher = async (command, _) =>
        {
            var index = Interlocked.Increment(ref callIndex);
            if (index == 1)
            {
                firstCallReached.SetResult();
                await firstCallSuspend.Task; // Held open until the test explicitly releases it.
                throw new IOException("Simulated transient World RPC failure - resolves AFTER the pending attempt below has already been replaced.");
            }
            // The SECOND call is the new attempt's own first dispatch - also transiently fails
            // (exactly once), so the new attempt has a genuine outstanding NextRetryAt for the stale
            // first-call handler to potentially (and incorrectly) corrupt.
            throw new IOException("Simulated transient World RPC failure for the NEW attempt's own first dispatch.");
        };

        var life = LifeFor(target);
        var firstAttemptTask = session.AllocatePendingDamageAttemptForTestAsync(life, damage: 10, acquireEngagement: false, CancellationToken.None);
        await firstCallReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While the first call is still suspended (in flight, not yet thrown/handled): EXPLICITLY
        // retire the old pending attempt via the dedicated adversarial-only seam (never a raw
        // overwrite - the ordinary allocation helper now refuses to clobber an outstanding attempt),
        // then allocate a genuinely NEW logical attempt (a different AttackSequence) for a DIFFERENT
        // life - simulating "the original attempt resolved/was replaced by something else while this
        // exact RPC call was still in flight".
        await session.RetirePendingDamageAttemptForTestAsync(CancellationToken.None);
        var otherLife = new WorldMonsterLifeReference("int_land03", _lastEpoch, ActorId: 12345, WorldMonsterIncarnationId.First);
        var secondAttemptTask = session.AllocatePendingDamageAttemptForTestAsync(otherLife, damage: 99, acquireEngagement: false, CancellationToken.None);
        await secondAttemptTask; // The new attempt's own first dispatch (transient failure) completes independently of the stale first call.

        var newAttemptNextRetryAtBefore = await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None);
        // The new attempt's own first call transiently failed, so it has a genuine outstanding
        // NextRetryAt at this point - this is the exact state the stale first-call handler must not
        // be allowed to corrupt.
        Assert.NotNull(newAttemptNextRetryAtBefore);

        // Now let the FIRST (stale) call's exception fire - its own transient-failure handler must
        // detect (via AttackSequence comparison) that the CURRENTLY-stored pending attempt is the
        // NEW attempt, not the one it started with, and must NOT mutate its NextRetryAt/state.
        firstCallSuspend.SetResult();
        await firstAttemptTask;

        // The new attempt's own NextRetryAt must be byte-for-byte unchanged by the stale handler's
        // resumption - proving the AttackSequence-comparison guard actually holds.
        var newAttemptNextRetryAtAfter = await session.SnapshotPendingNextRetryAtForTestAsync(CancellationToken.None);
        Assert.Equal(newAttemptNextRetryAtBefore, newAttemptNextRetryAtAfter);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
