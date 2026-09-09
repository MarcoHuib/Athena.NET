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

// Live-acceptance regression: a real Ragexe capture (attack-not-working.pcapng) showed 8 real
// client 0x0437 attack requests against genuinely-live projected monsters producing ZERO 0x08C8
// damage packets and, for 6 of the 8, not even a 0x0139 ZC_ATTACK_FAILURE_FOR_DISTANCE response -
// meaning those requests were silently dropped before ever reaching PerformDueRepeatAttackAsync's
// own range check.
//
// Root cause: HandleIroAttackRequestAsync ALWAYS deferred a due-now attack turn to
// RunRepeatAttackLoopAsync's own independently-scheduled background task (via _attackSignal)
// rather than executing it as part of the packet handler itself. Since a real client sends
// frequent 0x035F movement-continuation packets while auto-walking (including in direct response
// to a 0x0139 clif_movetoattack instruction), and HandleIroMovementAsync unconditionally clears
// _repeatAttack on every one of them (matching pinned unit_walktoxy's own unconditional
// unit_stop_attack call), a movement packet arriving between "0x0437 sets _repeatAttack" and "the
// background loop actually wakes and executes it" would silently clobber the state before
// PerformDueRepeatAttackAsync's own ReferenceEquals(_repeatAttack, expected) guard ever ran -
// producing exactly the observed symptom: a request that vanishes with no trace at all.
//
// Fix: matching pinned unit_attack's own "attackabletime already elapsed -> Attack NOW" branch
// (unit.cpp:2971-2978), a due-now attack turn is now executed SYNCHRONOUSLY within
// HandleIroAttackRequestAsync itself, closing the window entirely - RunAsync's own packet-read
// loop cannot process another incoming packet (including a movement request) until the handler
// returns. _attackExecutionGate (see that field's own doc comment on MapClientSession) serializes
// this inline execution against RunRepeatAttackLoopAsync's own background execution so the two
// can never run PerformDueRepeatAttackCoreAsync concurrently, and an explicit "is this turn still
// actually due" re-check inside that method (RepeatAttackState.NextAttackAt is mutated IN PLACE on
// the same object, so ReferenceEquals alone cannot detect an already-completed turn) prevents a
// genuine double-hit if both callers are woken for the same state.
[CollectionDefinition(nameof(MapClientSessionAttackSchedulingRaceTests), DisableParallelization = true)]
public sealed class MapClientSessionAttackSchedulingRaceTestsCollection;

[Collection(nameof(MapClientSessionAttackSchedulingRaceTests))]
public sealed class MapClientSessionAttackSchedulingRaceTests
{
    private const uint AccountId = 71;
    private const uint CharId = 73;

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

    // Weak enough (with the minimum weapon-ATK roll) that a single Knife hit does not one-shot
    // G_PORING's 55 HP - needed so tests C/D can observe a scheduled SECOND hit distinct from the
    // immediate first one, and so test A's "exactly one hit, not two" assertion is meaningful (a
    // one-shot kill would make a duplicate-hit bug invisible - there would be nothing left to
    // double-hit).
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

    private static byte[] MovementRequestPacket(ushort x, ushort y)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzRequestMove);
        packet[2] = (byte)(x >> 2);
        packet[3] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        packet[4] = (byte)(y << 4);
        packet[5] = 0xab;
        return packet;
    }

    private static readonly TimeSpan SocketReadTimeout = TimeSpan.FromSeconds(15);

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

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target, MonsterCombatStateStore CombatState)> SetupAsync(
        ushort playerX, ushort playerY, ushort monsterX, ushort monsterY, TimeProvider? timeProvider = null, CharacterGameplayState? gameplayState = null)
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
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(monsterX, monsterY), timeProvider ?? TimeProvider.System);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        _lastEpoch = epoch;
        var combatState = new MonsterCombatStateStore();
        combatState.Register(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value), target.Spawn.Mob.MaxHp);
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(MinWeaponAtkRoll), combatState);
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances);

        var gameplayPersistence = new RecordingGameplayStatePersistence(gameplayState ?? WeakFreshNovice());
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
        // A real World presence is required for HandleIroMovementAsync's own fresh-movement path
        // (test D exercises movement) - CharacterName must be non-empty for
        // EnterPlayerWorldAsync's own BuildCurrentPresence check to succeed.
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", playerX, playerY, 0, 0, 0, CharacterName: "TestNovice"));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15);
        await ReadExact(stream, 6);
        var equipList = await ReadDynamic(stream);
        Assert.NotEmpty(equipList);
        await ReadExact(stream, 4);

        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));
        Assert.Equal(target.ActorId, actorId);

        return (client, stream, session, run, target, combatState);
    }

    private uint CurrentHpOf(MonsterCombatStateStore combatState, MobInstance target) =>
        combatState.TryGet(new MonsterCombatKey(target.Map, _lastEpoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value)), out var state) ? state.CurrentHp : 0u;

    // Test A: attack is currently due -> client sends a valid 0x0437 for an IN-RANGE monster ->
    // immediately afterward client sends 0x035F -> the 0x0437 handler must complete the first
    // accepted attack (its own full wire projection) BEFORE the movement-driven cancellation could
    // ever erase it - exactly one 0x08C8, the expected 0x0977, no duplicate hit from the
    // background attack loop racing in afterward.
    [Fact]
    public async Task DueNowAttack_ImmediatelyFollowedByMovement_CompletesFirstHitBeforeCancellation_ExactlyOneHit()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, combatState) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, timeProvider: clock);
        using var _dispose = client;

        var hpBefore = CurrentHpOf(combatState, target);

        // Fire the attack and the movement request back-to-back, exactly like the live pcap's own
        // 0x0437 -> (auto-walk) -> 0x035F pattern - the movement packet is sent WITHOUT waiting for
        // the attack's own response first, so it can only be processed after
        // HandleIroAttackRequestAsync's own synchronous inline execution has already returned.
        await stream.WriteAsync(AttackPacket(target.ActorId));
        await stream.WriteAsync(MovementRequestPacket(76, 51));

        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(6)));
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
        Assert.True(damage > 0, "Expected the first attack to deal nonzero damage.");

        var hpInfoPacket = await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        Assert.Equal((short)PacketConstants.ZcHpInfo, BinaryPrimitives.ReadInt16LittleEndian(hpInfoPacket));
        var hpAfterFirstHit = BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(6));
        Assert.Equal(hpBefore - damage, hpAfterFirstHit);
        Assert.Equal(hpAfterFirstHit, CurrentHpOf(combatState, target));

        // The movement response follows - proving the movement request WAS processed (and, per the
        // existing, unchanged Attack_MovementRequest_CancelsActiveRepeatAttack behavior, cancelled
        // the repeat attack) but only AFTER the first hit's own complete wire sequence, never
        // interleaved with or before it.
        var movementResponse = await ReadExact(stream, 12);
        Assert.Equal((short)PacketConstants.ZcNotifyPlayerMove, BinaryPrimitives.ReadInt16LittleEndian(movementResponse));

        // No duplicate hit ever follows: advance the clock generously and confirm only a harmless
        // ping response arrives - the repeat attack was cancelled by the movement request (matching
        // pinned unit_walktoxy's own unconditional unit_stop_attack), and even if it hadn't been,
        // the scheduling fix itself must never produce a SECOND hit for the SAME due turn.
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));
        Assert.Equal(hpAfterFirstHit, CurrentHpOf(combatState, target));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test B: attack is currently due -> 0x0437 target is OUT OF RANGE -> exactly one 0x0139, no
    // damage -> a subsequent 0x035F is still allowed to cancel the (already-cleared) attack state
    // normally, exactly as before this fix.
    [Fact]
    public async Task DueNowAttack_TargetOutOfRange_SendsExactlyOne0x0139_NoDamage_SubsequentMovementStillAllowed()
    {
        var (client, stream, session, run, target, combatState) = await SetupAsync(playerX: 81, playerY: 64, monsterX: 72, monsterY: 78);
        using var _dispose = client;

        var hpBefore = CurrentHpOf(combatState, target);

        await stream.WriteAsync(AttackPacket(target.ActorId));

        var failurePacket = await ReadExact(stream, PacketConstants.ZcAttackFailureForDistanceLength);
        Assert.Equal((short)PacketConstants.ZcAttackFailureForDistance, BinaryPrimitives.ReadInt16LittleEndian(failurePacket));
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(failurePacket.AsSpan(2)));
        Assert.Equal(hpBefore, CurrentHpOf(combatState, target));

        // A subsequent movement request (the client's own real auto-walk-toward-target response to
        // 0x0139) must still be processed normally - no exception, no hang, ordinary movement ack.
        await stream.WriteAsync(MovementRequestPacket(75, 70));
        var movementResponse = await ReadExact(stream, 12);
        Assert.Equal((short)PacketConstants.ZcNotifyPlayerMove, BinaryPrimitives.ReadInt16LittleEndian(movementResponse));

        // Confirmed only a harmless ping follows - no duplicate 0x0139, no damage.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));
        Assert.Equal(hpBefore, CurrentHpOf(combatState, target));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test C: an existing repeat attack has NextAttackAt in the future (an already-ticking
    // cooldown) -> another 0x0437 retarget arrives -> it must NOT execute an immediate hit -
    // inherited cadence remains intact, matching pinned unit_attack's own "just change target/type"
    // behavior (a retarget mid-cooldown never forces an extra hit).
    [Fact]
    public async Task RetargetDuringExistingCooldown_DoesNotExecuteImmediateHit_InheritedCadenceIntact()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, combatState) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, timeProvider: clock);
        using var _dispose = client;

        // First attack: executes immediately (dueNow), establishing a real future NextAttackAt.
        await stream.WriteAsync(AttackPacket(target.ActorId));
        var firstDamage = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(firstDamage));
        await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        var hpAfterFirstHit = CurrentHpOf(combatState, target);
        Assert.True(hpAfterFirstHit > 0, "WeakFreshNovice's Knife hit must not one-shot G_PORING for this test to observe an intact cooldown.");

        // Immediately retarget the SAME target while the cooldown from the first hit is still
        // ticking - must NOT produce a second hit right now.
        await stream.WriteAsync(AttackPacket(target.ActorId));

        // Confirmed via a harmless ping round-trip landing next, with no damage packet observed in
        // between - the retarget did not force an immediate extra hit.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));
        Assert.Equal(hpAfterFirstHit, CurrentHpOf(combatState, target));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test D: a due-now 0x0437 (inline execution) and the background attack loop's own independent
    // wake genuinely overlap - must still produce exactly ONE attack execution, never two. Forced
    // deterministically by pre-arming a real background wait on _attackSignal (via a prior
    // WaitForNextDamagePacketAsync-shaped clock advance is not applicable here since there is no
    // prior state yet) - instead this drives the overlap directly through the documented
    // production mechanism: HandleIroAttackRequestAsync itself always releases _attackSignal after
    // a successful dueNow execution (see that method's own doc comment), which is exactly the
    // signal RunRepeatAttackLoopAsync reacts to for its OWN next scheduled turn - proving that
    // reaction never re-executes the SAME already-completed turn a second time.
    [Fact]
    public async Task DueNowAttackAndBackgroundLoopWake_Overlap_ProducesExactlyOneExecution_NeverTwo()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, combatState) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, timeProvider: clock);
        using var _dispose = client;

        var hpBefore = CurrentHpOf(combatState, target);

        // The inline dueNow execution below both executes the turn AND (per this fix's own design)
        // wakes RunRepeatAttackLoopAsync via _attackSignal.Release() immediately afterward - the
        // exact overlap this test targets: the background loop reacting to that same wake and
        // re-evaluating the (now rescheduled-to-the-future) RepeatAttackState must never re-execute
        // it, only recompute its own next sleep duration.
        await stream.WriteAsync(AttackPacket(target.ActorId));
        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
        await ReadExact(stream, PacketConstants.ZcHpInfoLength);

        var hpAfterOneHit = hpBefore - damage;
        Assert.Equal(hpAfterOneHit, CurrentHpOf(combatState, target));
        Assert.True(hpAfterOneHit > 0, "WeakFreshNovice's Knife hit must not one-shot G_PORING for this test to observe a bounded single hit.");

        // Give the background loop ample real wall-clock time to react to the wake signal and
        // (incorrectly, if this fix regressed) attempt a second execution of the SAME already-due-
        // and-already-completed turn - it must not, since NextAttackAt was already rescheduled to
        // the future BEFORE the wire notification (see PerformDueRepeatAttackCoreAsync's own doc
        // comment on that ordering) and this fix's own explicit re-check would reject a stale
        // attempt even if the loop DID wake concurrently.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));
        Assert.Equal(hpAfterOneHit, CurrentHpOf(combatState, target));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
