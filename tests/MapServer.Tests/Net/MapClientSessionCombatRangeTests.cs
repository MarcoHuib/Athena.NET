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

namespace Athena.Net.MapServer.Tests.Net;

// Live-bug regression: "A player can damage G_PORING from very far away." Proves the fix
// end-to-end via MapClientSession's real socket path (RunAsync/HandlePacketAsync) and the real
// production MonsterRegistry/MonsterCombatCoordinator/BasicAttackRangeResolver/ClientDistance/
// BasicAttackDistanceValidator services - no bypassing. Same real-socket integration pattern as
// MapClientSessionMonsterCombatTests, sequential within this class for the same documented xunit
// wire-ordering reason.
[CollectionDefinition(nameof(MapClientSessionCombatRangeTests), DisableParallelization = true)]
public sealed class MapClientSessionCombatRangeTestsCollection;

[Collection(nameof(MapClientSessionCombatRangeTests))]
public sealed class MapClientSessionCombatRangeTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

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

    // Strong enough to one/few-shot kill G_PORING's 55 HP armed with the Knife, keeping tests
    // fast/deterministic - matches MapClientSessionMonsterCombatTests' own StrongNovice fixture.
    private static CharacterGameplayState StrongNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 99, JobLevel: 10,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 100, CurrentSp: 100, MaxHp: 100, MaxSp: 100,
        StatPoints: 0, SkillPoints: 0, Strength: 99, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 99, Luck: 99);

    // Deliberately weak (canonical fresh-Novice 9/9/9/9/9/9, matching
    // MapClientSessionMonsterCombatTests' own WeakFreshNovice fixture) so even with the Knife
    // equipped and the minimum weapon-ATK roll, a single hit does not one-shot G_PORING's 55 HP -
    // needed so a test can observe a SECOND scheduled hit being withheld once the target moves
    // out of range, rather than the target already being dead after the first hit.
    private static CharacterGameplayState WeakFreshNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

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

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target, MonsterRegistry Registry)> SetupAsync(
        ushort playerX, ushort playerY, ushort monsterX, ushort monsterY,
        IMapCollisionProvider? collisionProvider = null, TimeProvider? timeProvider = null, Func<int, int, int>? rollWeaponAtk = null,
        CharacterGameplayState? gameplayState = null)
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
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator, new FixedCellSelector(monsterX, monsterY), timeProvider ?? TimeProvider.System);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules(rollWeaponAtk));
        var target = registry.AllInstances[0];

        var gameplayPersistence = new RecordingGameplayStatePersistence(gameplayState ?? StrongNovice());
        var inventoryListPersistence = new FixedInventoryListPersistence(KnifeEquipped());
        var inventoryPersistence = new RecordingInventoryPersistence();

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", playerX, playerY, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsters: registry, combat: combat,
            inventoryPersistence: inventoryPersistence, inventoryListPersistence: inventoryListPersistence,
            timeProvider: timeProvider, collisionProvider: collisionProvider);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", playerX, playerY, 0, 0, 0));

        // Consume the fixed 4-packet iRO bootstrap (0x0B18/0x0283/0x0ADE/0x02EB).
        await ReadExact(stream, 4 + 6 + 6 + 13);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        var equipList = await ReadDynamic(stream); // 0x0B39 equip item list (Knife)
        Assert.NotEmpty(equipList);
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd

        MobInstance? actualTarget = null;
        if (Math.Abs(playerX - monsterX) <= 14 && Math.Abs(playerY - monsterY) <= 14)
        {
            var spawn = await ReadDynamic(stream);
            var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));
            Assert.Equal(target.ActorId, actorId);
            actualTarget = target;
        }

        return (client, stream, session, run, actualTarget ?? target, registry);
    }

    // Live evidence reproduced: player far from a Range=1 Knife-equipped G_PORING must never take
    // damage on the very first 0x0437, and must instead receive the pinned 0x0139
    // ZC_ATTACK_FAILURE_FOR_DISTANCE response (clif_movetoattack, unit.cpp:3255-3258).
    [Fact]
    public async Task Attack_KnifeRangeOne_ClearlyDistantPoring_NoDamage_Receives0x0139()
    {
        // (81,64) and (72,78) from the live bug report - well outside any melee range, but still
        // within the 14-cell visibility range so the monster spawn broadcast still fires.
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 81, playerY: 64, monsterX: 72, monsterY: 78);
        using var _disposeClient = client;

        await stream.WriteAsync(AttackPacket(target.ActorId));

        var failurePacket = await ReadExact(stream, PacketConstants.ZcAttackFailureForDistanceLength);
        Assert.Equal((short)PacketConstants.ZcAttackFailureForDistance, BinaryPrimitives.ReadInt16LittleEndian(failurePacket));
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(failurePacket.AsSpan(2)));
        Assert.Equal((ushort)72, BinaryPrimitives.ReadUInt16LittleEndian(failurePacket.AsSpan(6)));
        Assert.Equal((ushort)78, BinaryPrimitives.ReadUInt16LittleEndian(failurePacket.AsSpan(8)));
        Assert.Equal((ushort)81, BinaryPrimitives.ReadUInt16LittleEndian(failurePacket.AsSpan(10)));
        Assert.Equal((ushort)64, BinaryPrimitives.ReadUInt16LittleEndian(failurePacket.AsSpan(12)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(failurePacket.AsSpan(14))); // Knife Range=1.

        Assert.Equal(55u, target.CurrentHp);
        Assert.True(target.IsAlive);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attack_TargetExactlyInAcceptedPinnedClientDistance_AttackWorks()
    {
        // Pinned distance_client: sqrt(1^2+0^2)-0.1 = 0.9 -> floor 0 <= range(1). Orthogonally
        // adjacent (dx=1,dy=0) is therefore in range for a Range=1 weapon.
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 76, monsterY: 51, rollWeaponAtk: (min, _) => min);
        using var _disposeClient = client;

        await stream.WriteAsync(AttackPacket(target.ActorId));

        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
        Assert.True(damage > 0);
        Assert.True(target.CurrentHp < 55u);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attack_TargetBeyondAcceptedDistance_AttackDoesNotWork()
    {
        // dx=3,dy=0: distance_client = sqrt(9)-0.1 = 2.9 -> floor 2, which is > range(1) - just
        // beyond the accepted pinned client-distance for a Range=1 weapon. (dx=2 gives
        // distance_client=1, which EQUALS range 1 and is therefore still in range - see
        // Attack_TargetExactlyInAcceptedPinnedClientDistance_AttackWorks's own sibling case.)
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 78, monsterY: 51);
        using var _disposeClient = client;

        await stream.WriteAsync(AttackPacket(target.ActorId));

        var failurePacket = await ReadExact(stream, PacketConstants.ZcAttackFailureForDistanceLength);
        Assert.Equal((short)PacketConstants.ZcAttackFailureForDistance, BinaryPrimitives.ReadInt16LittleEndian(failurePacket));
        Assert.Equal(55u, target.CurrentHp);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Server-authoritative re-validation on every repeat hit (mandatory - Porings now move):
    // first hit in range succeeds, then the target's authoritative position changes to somewhere
    // out of range before the next scheduled hit fires - that later hit must not deal damage.
    [Fact]
    public async Task Attack_MovingTargetLeavesRangeBetweenRepeatedHits_LaterHitDoesNotOccurRemotely()
    {
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target, registry) = await SetupAsync(
            playerX: 75, playerY: 51, monsterX: 76, monsterY: 51, timeProvider: clock, rollWeaponAtk: (min, _) => min, gameplayState: WeakFreshNovice());
        using var _disposeClient = client;

        await stream.WriteAsync(AttackPacket(target.ActorId));
        var firstHit = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(firstHit));
        var hpAfterFirstHit = target.CurrentHp;
        Assert.True(hpAfterFirstHit < 55u, "First in-range hit must deal damage.");
        Assert.True(target.IsAlive, "WeakFreshNovice's min-roll Knife hit must not one-shot G_PORING for this test to observe a second scheduled hit.");

        // The Poring "walks away" - simulate its authoritative position changing far out of range
        // (as MonsterRuntime's own idle-walk AI would do) directly on the real MobInstance, exactly
        // the scenario this task's own regression requires: server-owned repeat-attack scheduling
        // must re-read the CURRENT monster position on every hit, not cache the position from when
        // the attack started.
        Assert.True(target.TryStartIdleWalk([(76, 51), (95, 51)], orthogonalStepMs: 50, now: 1, nowOffset: DateTimeOffset.UnixEpoch, jitterMs: () => 0));
        target.AdvanceMovement(DateTimeOffset.UnixEpoch.AddMilliseconds(50));
        Assert.False(target.IsWalking); // Landed exactly on the far cell (95,51) - now well out of range.

        // Advance the clock far enough for the next repeat-attack hit to become due.
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));

        // No damage packet may follow - the deferred repeat-attack hit instead re-validates range
        // against the target's now-current (moved-away) position and sends the same pinned 0x0139
        // rejection an out-of-range attack always sends (see the "clearly distant" test above) -
        // never a second ZcNotifyAct3. A trailing ping round trip proves nothing else follows.
        var secondAttempt = await ReadExact(stream, PacketConstants.ZcAttackFailureForDistanceLength);
        Assert.Equal((short)PacketConstants.ZcAttackFailureForDistance, BinaryPrimitives.ReadInt16LittleEndian(secondAttempt));
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(secondAttempt.AsSpan(2)));
        Assert.Equal((ushort)95, BinaryPrimitives.ReadUInt16LittleEndian(secondAttempt.AsSpan(6))); // Target's CURRENT (moved) position.

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));

        Assert.Equal(hpAfterFirstHit, target.CurrentHp); // Unchanged since the first hit.
        Assert.True(target.IsAlive);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Existing death/drop/quest behavior must remain unchanged when the target is legitimately in
    // range - this is a narrow re-proof (not a duplicate of MapClientSessionMonsterCombatTests'
    // own broader quest-drop coverage) that the new range gate does not disturb the death path.
    [Fact]
    public async Task Attack_TargetInRange_DiesNormally_VanishPacketSent()
    {
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 76, monsterY: 51);
        using var _disposeClient = client;

        uint hpAfter = 55;
        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(target.ActorId));
            var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
            Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
            var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
            hpAfter = hpAfter > damage ? hpAfter - damage : 0;

            if (!target.IsAlive)
            {
                var vanish = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
                Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanish));
                Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanish[6]);
                break;
            }
        }

        Assert.False(target.IsAlive);
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // The exact live-bug reproduction case: distance 14 cells away, WITHIN monster visibility
    // range but far beyond any plausible attack range - proves visibility range (14) never
    // becomes attack range, which is the root cause this whole slice fixes.
    [Fact]
    public async Task Attack_TargetAtVisibilityRangeBoundary_NeverTreatedAsAttackRange()
    {
        var (client, stream, session, run, target, _) = await SetupAsync(playerX: 75, playerY: 51, monsterX: 89, monsterY: 51);
        using var _disposeClient = client;

        await stream.WriteAsync(AttackPacket(target.ActorId));

        var failurePacket = await ReadExact(stream, PacketConstants.ZcAttackFailureForDistanceLength);
        Assert.Equal((short)PacketConstants.ZcAttackFailureForDistance, BinaryPrimitives.ReadInt16LittleEndian(failurePacket));
        Assert.Equal(55u, target.CurrentHp);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // GeneratedItems.Knife.Range must be exactly 1, sourced from the pinned item_db - regression
    // for issue 1 at the wire-integration level (not just the importer/unit-test level).
    [Fact]
    public void GeneratedItems_Knife_RangeIsOne()
    {
        Assert.Equal(1, GeneratedItems.Knife.Range);
    }
}
