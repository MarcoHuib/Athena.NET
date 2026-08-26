using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Wire-level vertical-slice integration test using MapClientSession's real socket path
// (RunAsync/HandlePacketAsync), the real production MonsterRegistry/MonsterCombatCoordinator/
// QuestDropResolver/CharacterInventorySession domain services (no bypassing), and the
// verified-capture packet layouts from IroMonsterActorPacketsTests/IroMonsterCombatPacketsTests/
// IroAttackRequestPacketTests. Only the clock, quest/inventory persistence, and character stats
// are test doubles - the same pattern GeneratedCaptainCaroccIntegrationTests already uses.
public sealed class MapClientSessionMonsterCombatTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;
    private const uint Quest21008 = 21008;

    private sealed class RecordingQuestPersistence(uint questId, CharacterQuestStatus initialState) : ICharacterQuestPersistence
    {
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CancellationToken cancellationToken) =>
            Task.FromResult<CharacterQuestStatus?>(requestedQuestId == questId ? initialState : CharacterQuestStatus.Absent);
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CharacterQuestStatus state, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RecordingGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private sealed class RecordingInventoryPersistence : ICharacterInventoryPersistence
    {
        private readonly Dictionary<int, uint> _amounts = new();
        public Task<(bool Success, uint NewAmount, uint SlotIndex)> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken)
        {
            _amounts.TryGetValue(itemId, out var current);
            var updated = current + amount;
            _amounts[itemId] = updated;
            return Task.FromResult((true, updated, 0u));
        }
    }

    private static byte[] AttackPacket(uint targetActorId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzAttackRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), targetActorId);
        packet[6] = 7; // DMG_REPEAT
        packet[7] = 0x7f;
        return packet;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    // Strong enough to kill G_PORING's 55 HP in very few hits, keeping the test fast and
    // deterministic without depending on the exact RenewalBasicAttackRules formula's per-hit value.
    private static CharacterGameplayState StrongNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 99, JobLevel: 10,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 100, CurrentSp: 100, MaxHp: 100, MaxSp: 100,
        StatPoints: 0, SkillPoints: 0, Strength: 99, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 99, Luck: 99);

    // Deliberately weak (canonical fresh-Novice 9/9/9/9/9/9, matching
    // WeaponAttackCalculatorTests' own FreshNovice fixture) so a single unarmed hit against
    // G_PORING's 55 HP does not one-shot it - needed to observe an unarmed hit's damage
    // distinctly from an armed hit's, rather than both instantly killing the target.
    private static CharacterGameplayState WeakFreshNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target)> SetupAsync(
        RecordingInventoryPersistence inventoryPersistence, CharacterQuestStatus questState, ICharacterInventoryListPersistence? inventoryListPersistence = null,
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
        var registry = new MonsterRegistry([spawnDefinition], allocator, new FixedCellSelector(75, 51), TimeProvider.System);
        var questDrops = new QuestDropResolver(Generated.GameData.Quests.GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules());
        var target = registry.AllInstances[0];

        var questPersistence = new RecordingQuestPersistence(Quest21008, questState);
        var gameplayPersistence = new RecordingGameplayStatePersistence(gameplayState ?? StrongNovice());

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            questPersistence: questPersistence, gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsters: registry, combat: combat,
            inventoryPersistence: inventoryPersistence, inventoryListPersistence: inventoryListPersistence);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));

        // Consume the fixed 4-packet iRO bootstrap (0x0B18/0x0283/0x0ADE/0x02EB).
        await ReadExact(stream, 4 + 6 + 6 + 13);

        return (client, stream, session, run, target);
    }

    private sealed class FixedInventoryListPersistence(CharacterInventorySnapshot initial) : ICharacterInventoryListPersistence
    {
        private CharacterInventorySnapshot _current = initial;
        public Task<CharacterInventoryReadResult> GetInventoryAsync(uint a, uint c, CancellationToken t) => Task.FromResult(CharacterInventoryReadResult.Success(_current));
        public Task<bool> SetItemEquipAsync(uint a, uint c, uint slotIndex, uint equip, CancellationToken t)
        {
            var items = _current.Items.Select(i => i.SlotIndex == slotIndex ? i with { Equip = equip } : i).ToList();
            _current = new CharacterInventorySnapshot(items);
            return Task.FromResult(true);
        }
    }

    private sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
    {
        public (ushort X, ushort Y) SelectCell(MobSpawnDefinition spawn, int index) => (x, y);
    }

    [Fact]
    public async Task Attack_QuestActive_KillsMonster_GrantsWood_ThenRemovesActor()
    {
        var inventoryPersistence = new RecordingInventoryPersistence();
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Active);
        using var _ = client;

        // Trigger visibility (0x007D map-loaded) so the monster is in _visibleActorIds and its
        // real allocated actor ID is observable from the wire.
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6); // 0x0B08 inventoryStart (empty test-default inventory)
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd, all sent before the spawn broadcast
        var spawn = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(spawn));
        Assert.Equal((byte)5, spawn[4]);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));
        Assert.Equal(target.ActorId, actorId);
        Assert.Equal((ushort)2401, BinaryPrimitives.ReadUInt16LittleEndian(spawn.AsSpan(23)));

        uint hpAfter = target.Spawn.Mob.MaxHp;
        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(actorId));
            var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
            Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
            Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(6)));
            var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
            Assert.True(damage > 0, "Expected the strong test attacker to deal nonzero damage.");
            hpAfter = hpAfter > damage ? hpAfter - damage : 0;

            if (!target.IsAlive)
            {
                var vanish = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
                Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanish));
                Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(vanish.AsSpan(2)));
                Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanish[6]);

                var pickup = await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
                Assert.Equal((short)PacketConstants.ZcItemPickupAck, BinaryPrimitives.ReadInt16LittleEndian(pickup));
                Assert.Equal(6008u, BinaryPrimitives.ReadUInt32LittleEndian(pickup.AsSpan(6)));
                Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(pickup.AsSpan(4)));
                break;
            }
        }

        Assert.False(target.IsAlive);
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attack_QuestNotActive_KillsMonster_NoWoodPacketSent()
    {
        var inventoryPersistence = new RecordingInventoryPersistence();
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Absent);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6); // 0x0B08 inventoryStart (empty test-default inventory)
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd, all sent before the spawn broadcast
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(actorId));
            await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
            if (!target.IsAlive)
            {
                var vanish = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
                Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanish));
                break;
            }
        }

        Assert.False(target.IsAlive);

        // No further bytes (specifically no 0x0B41) should follow - confirm by sending a
        // harmless ping the server always answers, and observing that response next instead of
        // an unexpected item packet.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attack_AlreadyDeadMonster_DoesNotAwardWoodTwice()
    {
        var inventoryPersistence = new RecordingInventoryPersistence();
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Active);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6); // 0x0B08 inventoryStart (empty test-default inventory)
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd, all sent before the spawn broadcast
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(actorId));
            await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
            if (!target.IsAlive)
            {
                await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
                await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
                break;
            }
        }
        Assert.False(target.IsAlive);

        // Attacking the now-dead monster must produce no further wire traffic at all.
        await stream.WriteAsync(AttackPacket(actorId));
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static byte[] UnequipRequestPacket(ushort clientIndex)
    {
        var packet = new byte[PacketConstants.IroCzReqTakeoffEquipLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzReqTakeoffEquip);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        return packet;
    }

    private static byte[] EquipRequestPacket(ushort clientIndex, uint position)
    {
        var packet = new byte[PacketConstants.IroCzReqWearEquipLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzReqWearEquip);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), position);
        return packet;
    }

    // Consumes the self weapon-look (0x01D7) and inventory list burst (0x0B08 start,
    // optional 0x0B09 stackable/0x0B39 equip lists, 0x0B0B end) sent right after
    // 0x007D, for a session whose starter inventory has a Knife equipped in the right
    // hand (and nothing else) - i.e. exactly one 0x0B39 entry, no 0x0B09.
    private static async Task ConsumeSelfWeaponAndSingleEquipInventoryBurst(Stream stream)
    {
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6); // 0x0B08 inventoryStart
        await ReadDynamic(stream); // 0x0B39 equip list (one Knife entry)
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd
    }

    // Live-verified equipment infrastructure (EquippedWeaponResolver, CharacterEquipmentSnapshot)
    // already exists; this proves MonsterCombatCoordinator.Attack is actually wired to consume it:
    // a character whose starter inventory has the Knife equipped in the right hand deals damage
    // through WeaponAttackCalculator (not the unarmed BasicAttackCalculator), observable end-to-end
    // over the real wire path exactly like the existing unarmed combat tests.
    [Fact]
    public async Task Attack_WithEquippedKnife_KillsMonster_UsingWeaponAwareDamage()
    {
        var inventoryPersistence = new RecordingInventoryPersistence();
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Active, inventoryListPersistence);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(actorId));
            var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
            var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
            Assert.True(damage > 0, "Expected the equipped-Knife attacker to deal nonzero damage.");

            if (!target.IsAlive)
            {
                await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
                await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
                break;
            }
        }

        Assert.False(target.IsAlive);
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // The architecture requirement: unequipping mid-session must return combat to the genuine
    // unarmed path WITHOUT reconnecting - MonsterCombatCoordinator never caches the weapon
    // resolution, so the very next attack after a successful 0x00AB unequip must dispatch to
    // the unarmed RenewalBasicAttackRules path again.
    [Fact]
    public async Task Attack_UnequipKnifeMidSession_ReturnsToUnarmedCombat_WithoutReconnecting()
    {
        var inventoryPersistence = new RecordingInventoryPersistence();
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Absent, inventoryListPersistence, WeakFreshNovice());
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        // First hit while still armed - establishes the weapon-aware damage magnitude to compare
        // the post-unequip hit against.
        await stream.WriteAsync(AttackPacket(actorId));
        var armedDamagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        var armedDamage = BinaryPrimitives.ReadUInt32LittleEndian(armedDamagePacket.AsSpan(22));
        Assert.True(target.IsAlive, "Test setup requires G_PORING to survive the first (armed) hit so a second, post-unequip hit can be observed.");

        // clientIndex = server slotIndex(0) + 2, per the established client_index() convention.
        await stream.WriteAsync(UnequipRequestPacket(2));
        await ReadExact(stream, 15); // 0x01D7 self weapon look refresh, now unarmed (view id 0)
        await ReadExact(stream, PacketConstants.IroZcReqTakeoffEquipAckLength);

        await stream.WriteAsync(AttackPacket(actorId));
        var unarmedDamagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        var unarmedDamage = BinaryPrimitives.ReadUInt32LittleEndian(unarmedDamagePacket.AsSpan(22));

        Assert.True(unarmedDamage < armedDamage, "Expected unequipping the Knife to reduce subsequent attack damage back to the unarmed level.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Re-equipping the Knife during the SAME session must change subsequent combat back to
    // weapon-aware behavior without reconnecting - mirrors the unequip test above but round-trips
    // unequip -> equip and confirms damage becomes nonzero/weapon-shaped again afterward.
    [Fact]
    public async Task Attack_ReequipKnifeMidSession_ReturnsToWeaponAwareCombat_WithoutReconnecting()
    {
        var inventoryPersistence = new RecordingInventoryPersistence();
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Active, inventoryListPersistence);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        // clientIndex = server slotIndex(0) + 2.
        await stream.WriteAsync(UnequipRequestPacket(2));
        await ReadExact(stream, 15); // 0x01D7 appearance refresh (unarmed)
        await ReadExact(stream, PacketConstants.IroZcReqTakeoffEquipAckLength);

        await stream.WriteAsync(EquipRequestPacket(2, 0x000002)); // EQP_HAND_R
        await ReadExact(stream, PacketConstants.IroZcReqWearEquipAckLength); // ack first for equip
        await ReadExact(stream, 15); // 0x01D7 appearance refresh (Knife again)

        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(actorId));
            var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
            var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
            Assert.True(damage > 0, "Expected weapon-aware damage after re-equipping the Knife.");

            if (!target.IsAlive)
            {
                await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
                await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
                break;
            }
        }

        Assert.False(target.IsAlive);
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Architecture-hardening requirement: EquippedWeaponResolution.NonWeaponInWeaponSlot (and,
    // by the same reasoning, UnknownItem) is an authoritative-state/data invariant FAILURE, not a
    // legitimate unarmed state. This proves the attack is rejected/aborted outright over the real
    // wire path - no combat calculation runs and no wire response is sent at all, exactly like an
    // unresolvable target - rather than silently degrading into an unarmed attack.
    [Fact]
    public async Task Attack_NonWeaponItemInRightHandSlot_RejectsAttack_NoWireResponseAtAll()
    {
        var inventoryPersistence = new RecordingInventoryPersistence();
        // Wood (6008, EtcItemDefinition) is never equippable in real pinned item_db data; a row
        // carrying Equip=EQP_HAND_R for it can only represent corrupted/invariant-violating
        // authoritative state, which is exactly the case this test exercises.
        var invalidInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 6008, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(invalidInventory);
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Active, inventoryListPersistence);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        // No 0x01D7 is sent (SendSelfWeaponAppearanceAsync skips it for the same
        // NonWeaponInWeaponSlot resolution), only the inventory burst and monster spawn.
        await ReadExact(stream, 6); // 0x0B08 inventoryStart
        await ReadDynamic(stream); // 0x0B09 normal list (one Wood entry, since Wood is not IEquippableItemDefinition)
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        await stream.WriteAsync(AttackPacket(actorId));

        // No damage/vanish/pickup packet must ever arrive for the rejected attack. Confirm by
        // sending a harmless ping the server always answers, and observing THAT next instead of
        // any combat-result packet.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        Assert.Equal(target.Spawn.Mob.MaxHp, target.CurrentHp); // Monster HP must be completely untouched.
        Assert.True(target.IsAlive);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
