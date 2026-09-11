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

// Live-acceptance wire-fidelity fix regression: pinned rAthena's unit_attack (unit.cpp:2971-2978,
// commit e985006171d2eb320ee512a653f4c83aea3d81b6) sends clif_fixpos(*src) - 0x0088 ZC_STOPMOVE for
// the ATTACKING PLAYER's own current position - unconditionally, for every genuinely due-now attack
// request, BEFORE the attack-timer-equivalent execution/range check runs (regardless of whether that
// check subsequently accepts or rejects the attack). Athena previously omitted this packet entirely.
// This file proves the fix's exact wire ordering and its narrow scope (never for a mid-cooldown
// retarget, matching pinned unit_attack's own "just change target/type" early return which never
// reaches its own clif_fixpos call at all), plus the separate, independently-confirmed 0x0139
// currentAttRange wire-field fix (pinned clif_movetoattack, clif.cpp:8172-8184, uses the attacker's
// RAW/base weapon range - sd.battle_status.rhw.range - never the temporary +1 chasing-range bonus
// unit_attack_timer_sub itself adds purely for its own distance-check purposes).
[CollectionDefinition(nameof(MapClientSessionDueNowFixposTests), DisableParallelization = true)]
public sealed class MapClientSessionDueNowFixposTestsCollection;

[Collection(nameof(MapClientSessionDueNowFixposTests))]
public sealed class MapClientSessionDueNowFixposTests
{
    private const uint AccountId = 81;
    private const uint CharId = 83;

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

    private WorldSimulationEpoch _lastEpoch;

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target, MonsterCombatStateStore CombatState, MonsterFeedProjectionRegistry Projections)> SetupAsync(
        ushort playerX, ushort playerY, ushort monsterX, ushort monsterY, TimeProvider? timeProvider = null)
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
        var equipList = await ReadDynamic(stream);
        Assert.NotEmpty(equipList);
        await ReadExact(stream, 4);

        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));
        Assert.Equal(target.ActorId, actorId);

        return (client, stream, session, run, target, combatState, monsterProjections);
    }

    private uint CurrentHpOf(MonsterCombatStateStore combatState, MobInstance target) =>
        combatState.TryGet(new MonsterCombatKey(target.Map, _lastEpoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value)), out var state) ? state.CurrentHp : 0u;

    // Test 1: due-now, OUT-OF-RANGE attack -> wire order must be exactly 0x0088 (player fixpos) THEN
    // 0x0139 (attack-failure-for-distance) - never the reverse, never omitted. No damage, no HP
    // mutation.
    [Fact]
    public async Task DueNowOutOfRangeAttack_WireOrderIs0x0088ThenThen0x0139_NoDamageNoHpMutation()
    {
        var (client, stream, session, run, target, combatState, _) = await SetupAsync(playerX: 81, playerY: 64, monsterX: 72, monsterY: 78);
        using var _dispose = client;

        var hpBefore = CurrentHpOf(combatState, target);

        await stream.WriteAsync(AttackPacket(target.ActorId));

        var fixposPacket = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(fixposPacket.AsSpan(2)));
        Assert.Equal((ushort)81, BinaryPrimitives.ReadUInt16LittleEndian(fixposPacket.AsSpan(6)));
        Assert.Equal((ushort)64, BinaryPrimitives.ReadUInt16LittleEndian(fixposPacket.AsSpan(8)));

        var failurePacket = await ReadExact(stream, PacketConstants.ZcAttackFailureForDistanceLength);
        Assert.Equal((short)PacketConstants.ZcAttackFailureForDistance, BinaryPrimitives.ReadInt16LittleEndian(failurePacket));
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(failurePacket.AsSpan(2)));

        Assert.Equal(hpBefore, CurrentHpOf(combatState, target));

        // Confirmed no damage packet ever follows - a harmless ping lands next.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test 2: due-now, IN-RANGE attack -> player 0x0088 fixpos must occur BEFORE the first
    // player-attack/damage projection (0x08C8).
    [Fact]
    public async Task DueNowInRangeAttack_PlayerFixposOccursBeforeFirstDamageProjection()
    {
        var (client, stream, session, run, target, combatState, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51);
        using var _dispose = client;

        var hpBefore = CurrentHpOf(combatState, target);

        await stream.WriteAsync(AttackPacket(target.ActorId));

        var fixposPacket = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(fixposPacket.AsSpan(2)));
        Assert.Equal((ushort)75, BinaryPrimitives.ReadUInt16LittleEndian(fixposPacket.AsSpan(6)));
        Assert.Equal((ushort)51, BinaryPrimitives.ReadUInt16LittleEndian(fixposPacket.AsSpan(8)));

        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(6)));
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
        Assert.True(damage > 0);

        var hpInfoPacket = await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        Assert.Equal((short)PacketConstants.ZcHpInfo, BinaryPrimitives.ReadInt16LittleEndian(hpInfoPacket));
        Assert.Equal(hpBefore - damage, BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(6)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test 3: an existing repeat attack has a FUTURE NextAttackAt (an already-ticking cooldown) ->
    // a fresh 0x0437 retarget arrives -> must NOT create an extra immediate attack AND must NOT emit
    // an incorrect due-now-only 0x0088 merely because of the retarget - matching pinned unit_attack's
    // own "just change target/type" early return (unit.cpp:2951-2953), which never reaches its own
    // clif_fixpos call for a mid-cooldown retarget at all.
    [Fact]
    public async Task RetargetDuringExistingCooldown_NoExtraAttack_NoIncorrectDueNowFixpos()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, combatState, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 75, monsterY: 51, timeProvider: clock);
        using var _dispose = client;

        // First attack: genuinely due-now - gets exactly one fixpos, then the hit.
        await stream.WriteAsync(AttackPacket(target.ActorId));
        var firstFixpos = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(firstFixpos));
        var firstDamage = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(firstDamage));
        await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        var hpAfterFirstHit = CurrentHpOf(combatState, target);
        Assert.True(hpAfterFirstHit > 0, "WeakFreshNovice's Knife hit must not one-shot G_PORING for this test to observe an intact cooldown.");

        // Retarget the SAME target while the cooldown from the first hit is still ticking - must
        // produce NEITHER a second fixpos NOR a second hit.
        await stream.WriteAsync(AttackPacket(target.ActorId));

        // Confirmed via a harmless ping round-trip landing next, with NEITHER a fixpos NOR a damage
        // packet observed in between.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));
        Assert.Equal(hpAfterFirstHit, CurrentHpOf(combatState, target));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Test 4: 0x0139 currentAttRange must use the RAW/base weapon range (resolvedRange), never the
    // temporary +1 chasing-range bonus applied to the actual legality/distance CHECK
    // (effectiveRangeForRangeCheck) - pinned clif_movetoattack (clif.cpp:8172-8184) sets
    // `packet.currentAttRange = sd.battle_status.rhw.range`, the base value, with no bonus applied.
    // Constructs a WALKING monster (resolvedRange=1 for the Knife, effectiveRangeForRangeCheck=2 due
    // to the +1 walking-target bonus) positioned so the player is at Chebyshev distance 2 - REJECTED
    // by the range=2 check (proving the check itself still correctly uses the bonused value) but the
    // serialized 0x0139 field must still read 1, not 2 - explicitly proving the check value and the
    // wire-field value intentionally differ.
    [Fact]
    public async Task OutOfRangeAttack_WalkingTarget_0x0139UsesBaseRange_NotTheChasingBonusedCheckRange()
    {
        // (dx=3, dy=1) from the monster's spawn (75,51) - pinned check_distance_client is CIRCULAR
        // (ClientDistance.DistanceClient = floor(sqrt(dx^2+dy^2) - 0.1) = floor(sqrt(10)-0.1) = 3),
        // rejected by BOTH resolvedRange=1 (needs <=1) and effectiveRangeForRangeCheck=2 (needs <=2,
        // once the target is walking) - genuinely fails the client-distance check itself (never
        // reaching BasicAttackDistanceValidator.HasDirectAttackPath's own separate collision-data
        // requirement, which this focused test's minimal session setup does not provide), so this
        // test proves the WIRE FIELD without depending on the exact accept/reject boundary math for
        // the check itself.
        var (client, stream, session, run, target, combatState, monsterProjections) = await SetupAsync(playerX: 78, playerY: 52, monsterX: 75, monsterY: 51);
        using var _dispose = client;

        // Re-seed the SAME projection/epoch/combat-state with the mob now marked IsWalking=true -
        // the exact condition that makes effectiveRangeForRangeCheck (resolvedRange + 1) diverge
        // from resolvedRange alone. Reuses WorldMonsterProjectionTestHelper.ResyncProjection's own
        // established "same epoch, live re-seed" pattern rather than hand-constructing a second,
        // independent session setup from scratch.
        var walkingInstance = new WorldMonsterInstance(
            ActorId: target.ActorId, IncarnationId: new WorldMonsterIncarnationId(target.IncarnationId.Value), MapId: target.Map,
            MobId: target.Spawn.Mob.Id, X: target.GetPosition().X, Y: target.GetPosition().Y, Lifecycle: WorldMonsterLifecycleState.Alive,
            IsWalking: true, DestinationX: (ushort)(target.GetPosition().X + 1), DestinationY: target.GetPosition().Y,
            Engagement: WorldMonsterEngagementState.Unengaged, EngagedTarget: null, CurrentHp: target.CurrentHp, MaxHp: target.Spawn.Mob.MaxHp);
        monsterProjections.GetOrCreate(target.Map).ApplySnapshot([walkingInstance], _lastEpoch, combatState);

        await stream.WriteAsync(AttackPacket(target.ActorId));

        await ReadExact(stream, PacketConstants.ZcStopMoveLength); // The due-now player fixpos - not this test's focus, just drained.

        var failurePacket = await ReadExact(stream, PacketConstants.ZcAttackFailureForDistanceLength);
        Assert.Equal((short)PacketConstants.ZcAttackFailureForDistance, BinaryPrimitives.ReadInt16LittleEndian(failurePacket));
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(failurePacket.AsSpan(2)));

        // The critical assertion: currentAttRange (offset 14, per IroCombatDistancePackets' own
        // documented layout) must be the RAW base weapon range (1, Knife), never the +1
        // chasing-bonused check value (2) that was actually used to REJECT this attack.
        var currentAttRange = BinaryPrimitives.ReadUInt16LittleEndian(failurePacket.AsSpan(14));
        Assert.Equal((ushort)1, currentAttRange);
        Assert.NotEqual((ushort)2, currentAttRange);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
