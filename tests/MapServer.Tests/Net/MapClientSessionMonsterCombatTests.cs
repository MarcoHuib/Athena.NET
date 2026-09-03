using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Gameplay.Rates;
using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Confirmed empirically (start/end timestamps against a shared Stopwatch) that two [Fact]s in
// this class fully overlap in wall-clock time under this project's xunit 2.9.3 +
// xunit.runner.visualstudio 3.1.4 combination - contrary to xunit v2's classic "methods within
// one class run sequentially" default. The repeat-attack tests added below are real-socket/
// real-background-loop integration tests whose TCP read/write ordering is not safe under that
// concurrency (observed as intermittent "Connection reset by peer" and spurious 5-second
// WaitAsync timeouts once enough such tests exist in one file). DisableParallelization forces
// this one class's tests back to sequential execution without touching any other test class or
// global suite configuration.
[CollectionDefinition(nameof(MapClientSessionMonsterCombatTests), DisableParallelization = true)]
public sealed class MapClientSessionMonsterCombatTestsCollection;

// Wire-level vertical-slice integration test using MapClientSession's real socket path
// (RunAsync/HandlePacketAsync), the real production MonsterRegistry/MonsterCombatCoordinator/
// QuestDropResolver/CharacterInventorySession domain services (no bypassing), and the
// verified-capture packet layouts from IroMonsterActorPacketsTests/IroMonsterCombatPacketsTests/
// IroAttackRequestPacketTests. Only the clock, quest/inventory persistence, and character stats
// are test doubles - the same pattern GeneratedCaptainCaroccIntegrationTests already uses.
[Collection(nameof(MapClientSessionMonsterCombatTests))]
public sealed class MapClientSessionMonsterCombatTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;
    private const uint Quest21008 = 21008;
    private RecordingGameplayStatePersistence? _lastGameplayPersistence;

    private sealed class RecordingQuestPersistence(uint questId, CharacterQuestStatus initialState) : ICharacterQuestPersistence
    {
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CancellationToken cancellationToken) =>
            Task.FromResult<CharacterQuestStatus?>(requestedQuestId == questId ? initialState : CharacterQuestStatus.Absent);
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CharacterQuestStatus state, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RecordingGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public int Updates { get; private set; }
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            Updates++;
            return Task.FromResult<CharacterGameplayState?>(updated);
        }
    }

    // `existingRowCount` simulates however many DURABLE rows already exist in CharServer's own
    // row-id space (e.g. the starter Knife/Cotton Shirt/First Aid Box in
    // FixedInventoryListPersistence) - a brand-new stack is assigned the next DurableId after
    // those, in first-added order. DurableId is CharServer's row identity, never a runtime slot -
    // the caller (CharacterInventorySession/MapClientSession) is solely responsible for turning
    // IsNewRow+DurableId into a runtime SlotIndex via CharacterInventorySnapshot.WithNewItem.
    private sealed class RecordingInventoryPersistence(uint existingRowCount = 0) : ICharacterInventoryPersistence
    {
        private readonly Dictionary<int, uint> _amounts = new();
        private readonly Dictionary<int, uint> _durableIds = new();
        private readonly List<int> _newRowOrder = [];
        public bool FailNextCall { get; set; }

        public Task<InventoryAddPersistenceResult> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken)
        {
            if (FailNextCall)
            {
                FailNextCall = false;
                return Task.FromResult(InventoryAddPersistenceResult.Failed());
            }

            _amounts.TryGetValue(itemId, out var current);
            var updated = current + amount;
            _amounts[itemId] = updated;

            var newRowIndex = _newRowOrder.IndexOf(itemId);
            var isNewRow = newRowIndex < 0;
            if (isNewRow)
            {
                newRowIndex = _newRowOrder.Count;
                _newRowOrder.Add(itemId);
            }
            if (!_durableIds.TryGetValue(itemId, out var durableId))
            {
                durableId = existingRowCount + (uint)newRowIndex + 1;
                _durableIds[itemId] = durableId;
            }

            return Task.FromResult(new InventoryAddPersistenceResult(true, updated, durableId, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0, isNewRow));
        }

        // Not exercised by this file's reward-path tests (they only ever add items) - a minimal
        // stub is sufficient here, matching this fixture's existing narrow scope.
        public Task<InventoryConsumePersistenceResult> ConsumeItemAsync(uint accountId, uint charId, uint durableId, uint amount, CancellationToken cancellationToken) =>
            Task.FromResult(InventoryConsumePersistenceResult.Failed());
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

    // Every hit against a monster the attacking session can already see (true for every test in
    // this file - the spawn packet is always consumed before any attack) is followed immediately
    // by ZC_HP_INFO (0x0977), per pinned status_damage -> mob_damage's own ordering - see
    // PacketConstants.ZcHpInfo's own doc comment. Centralizes the now-two-packet read this file's
    // many hit-loop tests need, so each call site doesn't have to know the wire shape itself; the
    // returned tuple lets a test still assert on the damage packet's own fields exactly as before.
    private static async Task<(byte[] Damage, byte[] HpInfo)> ReadDamageAndHpInfoAsync(Stream stream)
    {
        var damage = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        var hpInfo = await ReadExact(stream, PacketConstants.ZcHpInfoLength);
        Assert.Equal((short)PacketConstants.ZcHpInfo, BinaryPrimitives.ReadInt16LittleEndian(hpInfo));
        return (damage, hpInfo);
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

    // Pins WeaponAttackCalculator's rnd_value(atkmin, atkmax) roll to its minimum - the
    // repeat-attack tests below need EVERY hit's damage to be small and deterministic (never
    // randomly lethal against G_PORING's 55 HP), not merely "usually non-lethal" as an
    // unpinned Random.Shared roll would give.
    private static int MinWeaponAtkRoll(int min, int max) => min;

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target)> SetupAsync(
        RecordingInventoryPersistence inventoryPersistence, CharacterQuestStatus questState, ICharacterInventoryListPersistence? inventoryListPersistence = null,
        CharacterGameplayState? gameplayState = null, TimeProvider? timeProvider = null, Func<int, int, int>? rollWeaponAtk = null,
        MobDefinition? mobDefinition = null, GameplayRateOptions? rates = null)
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
        var spawnDefinition = new MobSpawnDefinition(mobDefinition ?? GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
        var questDrops = new QuestDropResolver(Generated.GameData.Quests.GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules(rollWeaponAtk));
        var target = registry.AllInstances[0];

        var questPersistence = new RecordingQuestPersistence(Quest21008, questState);
        var gameplayPersistence = new RecordingGameplayStatePersistence(gameplayState ?? StrongNovice());
        _lastGameplayPersistence = gameplayPersistence;

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            questPersistence: questPersistence, gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsters: registry, combat: combat,
            inventoryPersistence: inventoryPersistence, inventoryListPersistence: inventoryListPersistence,
            timeProvider: timeProvider, rates: rates);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));

        // Consume the fixed 4-packet iRO bootstrap (0x0B18/0x0283/0x0ADE/0x02EB) plus the
        // variable-length 0x0B32 skill list that now always follows it.
        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream);

        return (client, stream, session, run, target);
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

    private sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
    {
        public bool TrySelectCell(MobSpawnDefinition spawn, int index, out MobPosition position)
        {
            position = new MobPosition(x, y);
            return true;
        }
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
            var (damagePacket, hpInfoPacket) = await ReadDamageAndHpInfoAsync(stream);
            Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
            Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(6)));
            var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
            Assert.True(damage > 0, "Expected the strong test attacker to deal nonzero damage.");
            hpAfter = hpAfter > damage ? hpAfter - damage : 0;

            // HP-info must carry the authoritative post-damage HP - both the value this test
            // independently derives from the damage packet AND the monster's own real CurrentHp
            // (never a stale/pre-damage value), plus the correct actor ID and unchanged MaxHp.
            Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(2)));
            Assert.Equal(hpAfter, BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(6)));
            Assert.Equal(target.CurrentHp, BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(6)));
            Assert.Equal(target.Spawn.Mob.MaxHp, BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(10)));

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
        Assert.Equal(0, _lastGameplayPersistence!.Updates);
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Visibility semantics (requirement 5): pinned mob_damage only sends the HP bar to sessions
    // already in the mob's own dmglog AND within AREA_SIZE - never a blind broadcast. This
    // session's closest analog is _visibleActorIds (the same discovery-dedup set
    // SendVisibleMonsterActorsAsync already uses): a session that never received the monster's own
    // discovery/actor-entry packet (0x007D/map-load was never sent here, so _visibleActorIds never
    // marked this actor visible) must NOT receive ZC_HP_INFO even though the attack itself still
    // lands (attack target resolution uses MonsterRegistry.TryGetInstance + range only, never
    // visibility - see MapClientSession.cs:1608-1610 - so this is a genuine "attacker without a
    // discovery packet" case, not an artificial one).
    [Fact]
    public async Task Attack_TargetNeverDiscoveredByThisSession_SendsDamageButNotHpInfo()
    {
        var inventoryPersistence = new RecordingInventoryPersistence();
        // WeakFreshNovice so the attack is guaranteed non-lethal (this test asserts on the
        // ordinary hit sequence only, never the death/vanish sequence).
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Absent, gameplayState: WeakFreshNovice());
        using var _ = client;

        // Deliberately skip 0x007D (map-load) - the monster is never added to this session's
        // _visibleActorIds, matching a session that has not (yet) been told this actor exists.
        await stream.WriteAsync(AttackPacket(target.ActorId));
        var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22)) > 0);
        Assert.True(target.IsAlive, "Test requires a non-lethal first hit so the ordinary (non-death) packet sequence is what's being observed.");

        // No ZC_HP_INFO must follow - confirmed by observing a harmless ping reply next instead.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

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
            await ReadDamageAndHpInfoAsync(stream);
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
    public async Task Attack_SourceBackedNonzeroMob_AppliesRatesPersistsThenSendsProgressionAndVisuals()
    {
        // Pinned db/re/mob_db.yml Id 1002, not a G_PORING override. Normal Poring's raw
        // BaseExp/JobExp are 150/40; all other fields below are from the same pinned block.
        var poring = new MobDefinition(1002, "PORING", "Poring", 1, 55, 1, 1, 2, 5,
            6, 1, 1, 0, 6, 5, 1, 400, 1872, 672, 480, 150, 40,
            MobMode.CanMove | MobMode.CanAttack,
            new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "legacy/rathena/db/re/mob_db.yml", 136));
        var rates = new GameplayRateOptions { BaseExpRate = 500, JobExpRate = 200 };
        var (client, stream, session, run, target) = await SetupAsync(
            new RecordingInventoryPersistence(), CharacterQuestStatus.Absent,
            gameplayState: WeakFreshNovice(), mobDefinition: poring, rates: rates);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15);
        await ReadExact(stream, 6);
        await ReadExact(stream, 4);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian((await ReadDynamic(stream)).AsSpan(5));
        var progressionIds = new List<short>();

        for (var i = 0; i < 30 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(actorId));
            await ReadDamageAndHpInfoAsync(stream);
            if (!target.IsAlive)
            {
                while (true)
                {
                    var header = await ReadExact(stream, 2);
                    var id = BinaryPrimitives.ReadInt16LittleEndian(header);
                    var length = id switch
                    {
                        PacketConstants.ZcLongLongParameterChange => 12,
                        PacketConstants.ZcNotifyExperience => PacketConstants.ZcNotifyExperienceLength,
                        PacketConstants.ZcNotifyEffect => PacketConstants.ZcNotifyEffectLength,
                        PacketConstants.ZcParameterChange => 8,
                        PacketConstants.ZcNotifyVanish => PacketConstants.ZcNotifyVanishLength,
                        _ => throw new InvalidDataException($"Unexpected post-death packet 0x{id:X4}."),
                    };
                    await ReadExact(stream, length - 2);
                    if (id == PacketConstants.ZcNotifyVanish) break;
                    progressionIds.Add(id);
                }
            }
        }

        Assert.False(target.IsAlive);
        Assert.Equal(1, _lastGameplayPersistence!.Updates);
        Assert.Equal((ushort)2, session.GameplayState!.State.BaseLevel);
        Assert.Equal(202UL, session.GameplayState.State.BaseExperience); // 150 * 500% - 548
        Assert.Equal((ushort)2, session.GameplayState.State.JobLevel);
        Assert.Equal(9UL, session.GameplayState.State.JobExperience); // pinned single-level overcarry cap
        Assert.Equal(2, progressionIds.Count(id => id == PacketConstants.ZcNotifyExperience));
        Assert.Equal(2, progressionIds.Count(id => id == PacketConstants.ZcNotifyEffect));

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
            await ReadDamageAndHpInfoAsync(stream);
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
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 1); // Knife already occupies slot 0.
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
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
            var (damagePacket, _) = await ReadDamageAndHpInfoAsync(stream);
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
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
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
        var (armedDamagePacket, _) = await ReadDamageAndHpInfoAsync(stream);
        var armedDamage = BinaryPrimitives.ReadUInt32LittleEndian(armedDamagePacket.AsSpan(22));
        Assert.True(target.IsAlive, "Test setup requires G_PORING to survive the first (armed) hit so a second, post-unequip hit can be observed.");

        // clientIndex = server slotIndex(0) + 2, per the established client_index() convention.
        await stream.WriteAsync(UnequipRequestPacket(2));
        await ReadExact(stream, 15); // 0x01D7 self weapon look refresh, now unarmed (view id 0)
        await ReadExact(stream, PacketConstants.IroZcReqTakeoffEquipAckLength);

        await stream.WriteAsync(AttackPacket(actorId));
        var (unarmedDamagePacket, _) = await ReadDamageAndHpInfoAsync(stream);
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
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 1); // Knife already occupies slot 0.
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
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
            var (damagePacket, _) = await ReadDamageAndHpInfoAsync(stream);
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
        var invalidInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 6008, 1, 0x000002, true, 0, 0, 0)]);
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

    // Inventory-consistency fix: proves requirements 7-12 end-to-end over the real wire path
    // with the exact three-row starter inventory shape this task describes (Knife equipped,
    // Cotton Shirt equipped, First Aid Box unequipped - three server slots 0/1/2 regardless of
    // equip state). A first Wood reward must land at slot 3 (client index 5), immediately be
    // visible in MapClientSession.Inventory/Equipment without reconnecting, and must not disturb
    // the existing equipped items' slots - proven by successfully unequipping the Knife by its
    // ORIGINAL slot-derived client index afterward. A second reward for the same stack must
    // increment the SAME slot's amount rather than creating a fourth row.
    [Fact]
    public async Task Attack_KillsTwoMonsters_WoodStacksInStableFourthSlot_RuntimeSnapshotUpdatesImmediately_ExistingEquipmentSlotsUnaffected()
    {
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 3);
        var startingInventory = new CharacterInventorySnapshot(
        [
            new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0), // Knife, equipped (slot 0)
            new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, 2301, 1, 0x000010, true, 0, 0, 0), // Cotton Shirt, equipped (slot 1)
            new CharacterInventoryItem(DurableId: 3, SlotIndex: 2, 23484, 1, 0, true, 0, 0, 0), // First Aid Box, unequipped (slot 2)
        ]);
        var inventoryListPersistence = new FixedInventoryListPersistence(startingInventory);
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Active, inventoryListPersistence);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6); // 0x0B08 inventoryStart
        await ReadDynamic(stream); // 0x0B09 normal list (First Aid Box)
        await ReadDynamic(stream); // 0x0B39 equip list (Knife, Cotton Shirt)
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        // Requirement 1-6 setup check: three starting rows at the expected server slots, matching
        // the same InStableSlotOrder ordering the CharServer-side fix now uses.
        Assert.Equal(3, session.Inventory!.Items.Count);
        Assert.Equal(0u, session.Inventory.Items.Single(i => i.ItemId == 1201).SlotIndex);
        Assert.Equal(1u, session.Inventory.Items.Single(i => i.ItemId == 2301).SlotIndex);
        Assert.Equal(2u, session.Inventory.Items.Single(i => i.ItemId == 23484).SlotIndex);

        // --- First kill: Wood lands at slot 3 / client index 5, runtime snapshot updates immediately ---
        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(actorId));
            await ReadDamageAndHpInfoAsync(stream);
            if (!target.IsAlive)
            {
                await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
                var pickup = await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
                var clientIndex = BinaryPrimitives.ReadUInt16LittleEndian(pickup.AsSpan(2));
                Assert.Equal((ushort)5, clientIndex); // server slot 3 + 2.
                break;
            }
        }
        Assert.False(target.IsAlive);

        // Requirement 7: MapClientSession.Inventory immediately contains the new item in the
        // correct slot without reconnecting.
        Assert.Equal(4, session.Inventory!.Items.Count);
        var wood = session.Inventory.Items.Single(i => i.ItemId == 6008);
        Assert.Equal(3u, wood.SlotIndex);
        Assert.Equal(1u, wood.Amount);

        // Requirement 11: existing equipped Knife/Cotton Shirt server indices remain stable.
        Assert.Equal(0u, session.Inventory.Items.Single(i => i.ItemId == 1201).SlotIndex);
        Assert.Equal(1u, session.Inventory.Items.Single(i => i.ItemId == 2301).SlotIndex);

        // Requirement 10: CharacterEquipmentSnapshot remains correctly derived from the updated
        // inventory snapshot (Knife is still the resolved right-hand item).
        Assert.Equal(1201, session.Equipment!.RightHandItemId);

        // Requirement 12: same-session equip/unequip using the ORIGINAL (unaffected) Knife slot
        // remains valid after the inventory add - clientIndex = server slotIndex(0) + 2.
        await stream.WriteAsync(UnequipRequestPacket(2));
        await ReadExact(stream, 15); // 0x01D7 appearance refresh (unarmed)
        var unequipAck = await ReadExact(stream, PacketConstants.IroZcReqTakeoffEquipAckLength);
        Assert.Equal((short)PacketConstants.IroZcReqTakeoffEquipAck, BinaryPrimitives.ReadInt16LittleEndian(unequipAck));
        Assert.Null(session.Equipment!.RightHandItemId); // Unequip actually succeeded.
        // Wood must still be exactly where it was - unrelated equip mutation must not disturb it.
        Assert.Equal(3u, session.Inventory!.Items.Single(i => i.ItemId == 6008).SlotIndex);

        // Re-equip so the second kill again uses weapon-aware damage - not required by the
        // consistency fix itself, but keeps the second kill fast/deterministic like the first.
        await stream.WriteAsync(EquipRequestPacket(2, 0x000002));
        await ReadExact(stream, PacketConstants.IroZcReqWearEquipAckLength);
        await ReadExact(stream, 15);

        // --- Second reward: the SAME Wood stack increments in the SAME slot, no fifth row ---
        // Exercised directly through a second CharacterInventorySession.AddItemAsync call rather
        // than a second monster kill - this test's single MonsterRegistry instance has already
        // died and real respawn/second-instance timing is unrelated to the inventory-consistency
        // fix under test (requirement 5/9: stack increment preserves slot, no new row).
        var woodDurableId = session.Inventory!.Items.Single(i => i.ItemId == 6008).DurableId;
        var secondAddResult = await new CharacterInventorySession(AccountId, CharId, inventoryPersistence)
            .AddItemAsync(GeneratedItems.Wood, 1, CancellationToken.None);
        Assert.True(secondAddResult.Success);
        Assert.False(secondAddResult.IsNewRow); // Same durable row, not a new one.
        Assert.Equal(woodDurableId, secondAddResult.DurableId);
        Assert.Equal(2u, secondAddResult.Item!.Value.Amount);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Requirement 13: a failed CharServer persistence must not mutate the MapServer runtime
    // inventory snapshot and must not notify the client - proven directly against
    // MapClientSession's reward path without depending on monster-kill timing.
    [Fact]
    public async Task Attack_KillsMonster_PersistenceFailure_DoesNotMutateRuntimeSnapshot_DoesNotNotifyClient()
    {
        var inventoryPersistence = new RecordingInventoryPersistence { FailNextCall = true };
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Active);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6); // 0x0B08 inventoryStart (empty test-default inventory)
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        var inventoryCountBefore = session.Inventory!.Items.Count;

        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(actorId));
            await ReadDamageAndHpInfoAsync(stream);
            if (!target.IsAlive)
            {
                await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
                break; // No 0x0B41 must follow - checked below via the ping probe.
            }
        }
        Assert.False(target.IsAlive);

        Assert.Equal(inventoryCountBefore, session.Inventory!.Items.Count); // Runtime snapshot untouched.

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next)); // No pickup ack was sent.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // DMG_REPEAT server-owned attack repetition (ai/iro-2026-wire.md's documented future work,
    // now implemented). This block proves: ONE 0x0437 request produces MULTIPLE authoritative
    // hits over the source-backed attack-delay cadence with no further client packet, using a
    // ControllableTimeProvider (never real-time sleep) to drive RunRepeatAttackLoopAsync
    // deterministically - matching the task's "no unbounded/sleep-based test waits" rule.
    //
    // WeakFreshNovice (canonical 9/9/9/9/9/9) with the equipped starter Knife yields
    // AttackDelayCalculator.AttackDelayMs = 1160ms, hand-derived the same way. Used wherever a
    // test needs a non-lethal per-hit damage magnitude against G_PORING's 55 HP (matching the
    // existing Attack_UnequipKnifeMidSession... test's own proven non-lethal single-hit behavior).
    private const int WeakNoviceKnifeDelayMs = 1160;

    // Advances the fake clock by exactly one source-backed attack-delay interval and reads the
    // resulting damage packet. Deliberately does NOT wait for a fresh timer registration first
    // (unlike MovementSchedulerTestHelpers.AdvanceEntireWalkAsync, which starts a walk from an
    // otherwise-idle loop): by the time a test calls this, the repeat-attack loop has ALREADY
    // rescheduled and re-armed its timer as part of processing the previous hit (synchronously,
    // before that hit's damage packet was even written to the wire) - waiting for a NEW
    // registration here would wait for the NEXT hit's reschedule, which cannot happen until this
    // advance fires the ALREADY-armed one first (a real chicken-and-egg deadlock, not a flaky
    // race). The very first call after an immediate (non-scheduled) hit is the one exception
    // this helper is not used for - callers read that hit directly via ReadExact instead.
    // Deadlock/hang safety bound only, not part of the asserted combat behavior. Wider than this
    // project's usual 5s bound (see MapClientSessionCombatRangeTests.SocketReadTimeout for the
    // same reasoning and an earlier reproduction): this method's real-socket read races a
    // genuinely contended CI runner - reproduced flaky under a 2-CPU-constrained Linux run of the
    // full Release suite (never in isolation), matching this class's own doc comment already
    // flagging "spurious 5-second WaitAsync timeouts" as a known symptom of this file's
    // real-socket/real-background-loop integration tests. Scoped to ONLY this packet-read/wait
    // path - the one call site that actually reproduced the flake; run.WaitAsync,
    // DisposeAsync().WaitAsync, and this file's other 5s lifecycle bounds are untouched since none
    // of those have independently failed.
    private static readonly TimeSpan SocketReadTimeout = TimeSpan.FromSeconds(15);

    private static async Task<byte[]> WaitForNextDamagePacketAsync(Stream stream, ControllableTimeProvider clock, int delayMs)
    {
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(delayMs));
        var (damage, _) = await ReadDamageAndHpInfoAsync(stream).WaitAsync(SocketReadTimeout);
        return damage;
    }

    [Fact]
    public async Task Attack_OneRepeatRequest_ProducesMultipleHits_WithoutAnotherClientPacket()
    {
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 1);
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Absent, inventoryListPersistence, gameplayState: WeakFreshNovice(), timeProvider: clock, rollWeaponAtk: MinWeaponAtkRoll);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        // Exactly ONE client attack request.
        await stream.WriteAsync(AttackPacket(actorId));

        // First hit fires immediately (pinned unit_attack: attackabletime already elapsed ->
        // unit_attack_timer(INVALID_TIMER, ...) runs right away, unit.cpp:2971-2978) - no clock
        // advance needed for it.
        var (firstHit, _) = await ReadDamageAndHpInfoAsync(stream);
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(firstHit.AsSpan(22)) > 0);
        Assert.True(target.IsAlive, "WeakFreshNovice's Knife hit must not one-shot G_PORING for this test to observe a second hit.");

        // Second and third hits arrive from the server-owned loop alone, one source-backed delay
        // apart each, with NO further 0x0437 sent.
        var secondHit = await WaitForNextDamagePacketAsync(stream, clock, WeakNoviceKnifeDelayMs);
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(secondHit.AsSpan(22)) > 0);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attack_RepeatedHits_RespectSourceBackedMinimumInterval()
    {
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 1);
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Absent, inventoryListPersistence, gameplayState: WeakFreshNovice(), timeProvider: clock, rollWeaponAtk: MinWeaponAtkRoll);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        await stream.WriteAsync(AttackPacket(actorId));
        await ReadDamageAndHpInfoAsync(stream); // First (immediate) hit.
        Assert.True(target.IsAlive);

        // Advancing less than the full source-backed delay must NOT produce a second hit yet. The
        // loop already rescheduled/re-armed its timer synchronously while processing the first
        // hit above (before its damage packet was even written to the wire), so no additional
        // registration-wait is needed here.
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(WeakNoviceKnifeDelayMs - 1));
        Assert.Equal(0, client.Available);

        // Crossing the remaining 1ms releases exactly the expected next hit.
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        var (secondHit, _) = await ReadDamageAndHpInfoAsync(stream);
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(secondHit.AsSpan(22)) > 0);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attack_TargetDeathDuringRepeat_StopsRepetition_NoFurtherDamageEvents()
    {
        // WeakFreshNovice + Knife deals enough damage per hit that G_PORING's 55 HP dies within a
        // few repeat hits (not the first) - proving the loop stops issuing damage events once the
        // target is dead, rather than merely "the test stopped attacking".
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 1);
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Absent, inventoryListPersistence, gameplayState: WeakFreshNovice(), timeProvider: clock, rollWeaponAtk: MinWeaponAtkRoll);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        await stream.WriteAsync(AttackPacket(actorId));

        const int weakNoviceKnifeDelayMs = 1160; // WeakFreshNovice (DEX/AGI=9) + Knife, hand-derived.
        var isDead = false;
        for (var i = 0; i < 30 && !isDead; i++)
        {
            // Advance BEFORE reading (except the very first, already-immediate hit): the loop may
            // kill the target as part of THIS advance, so the resulting damage packet must always
            // be read off the wire before checking target.IsAlive - checking the alive flag first
            // and conditionally skipping the read (as an earlier version of this test did) leaves
            // that hit's already-written damage packet unread, corrupting every later read in the
            // test with a stale packet boundary.
            if (i > 0) await clock.AdvanceAsync(TimeSpan.FromMilliseconds(weakNoviceKnifeDelayMs));
            await ReadDamageAndHpInfoAsync(stream);
            isDead = !target.IsAlive;
            if (isDead)
            {
                var vanish = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
                Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanish));
            }
        }
        Assert.True(isDead);

        // No further damage/vanish traffic must ever follow death - confirmed by advancing time
        // generously with no new attack request and observing only a harmless ping response next.
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(weakNoviceKnifeDelayMs * 5));
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attack_NewTargetRequest_ReplacesPriorRepeatTarget()
    {
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 1);
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var clock = new ControllableTimeProvider();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();
        using var _ = client;

        var allocator = new WorldActorIdAllocator();
        var spawnA = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var spawnB = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnA, spawnB], allocator.Allocate, new SequentialCellSelector((75, 51), (80, 55)), TimeProvider.System);
        var questDrops = new QuestDropResolver(Generated.GameData.Quests.GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules(MinWeaponAtkRoll));
        var targetA = registry.AllInstances[0];
        var targetB = registry.AllInstances[1];

        var questPersistence = new RecordingQuestPersistence(Quest21008, CharacterQuestStatus.Absent);
        var gameplayPersistence = new RecordingGameplayStatePersistence(WeakFreshNovice());

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            questPersistence: questPersistence, gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsters: registry, combat: combat,
            inventoryPersistence: inventoryPersistence, inventoryListPersistence: inventoryListPersistence,
            timeProvider: clock);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));
        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream); // 0x0B32 skill list

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawnPacketA = await ReadDynamic(stream);
        var spawnPacketB = await ReadDynamic(stream);
        Assert.Equal(targetA.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(spawnPacketA.AsSpan(5)));
        Assert.Equal(targetB.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(spawnPacketB.AsSpan(5)));

        // Start a repeat attack against target A.
        await stream.WriteAsync(AttackPacket(targetA.ActorId));
        var (firstHit, _) = await ReadDamageAndHpInfoAsync(stream);
        Assert.Equal(targetA.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(firstHit.AsSpan(6)));
        Assert.True(targetA.IsAlive, "WeakFreshNovice's Knife hit must not one-shot G_PORING for this test to observe the retarget.");

        // Before the next scheduled hit against A, retarget to B - pinned unit_attack's "just
        // change target/type" behavior (unit.cpp:2951-2953): while an attack timer is already
        // pending, a new request only changes WHICH target the already-scheduled timer will hit
        // next - it does NOT reset attackabletime or force an immediate hit. So target B's first
        // hit only arrives after A's remaining cooldown elapses, not immediately.
        await stream.WriteAsync(AttackPacket(targetB.ActorId));
        // Synchronize on the retarget actually being processed before advancing the fake clock:
        // WriteAsync only guarantees the bytes were queued, not that MapClientSession's packet
        // loop already handled them. 0x0B1C is processed strictly after the just-sent 0x0437 on
        // the same TCP stream and always elicits an immediate reply (same synchronization idiom
        // the existing ping-probe tests in this file already use).
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        await ReadExact(stream, 2);
        var secondHit = await WaitForNextDamagePacketAsync(stream, clock, WeakNoviceKnifeDelayMs);
        Assert.Equal(targetB.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(secondHit.AsSpan(6)));

        // Advancing time again must continue hitting B, never A again.
        var targetAHpAfterItsOnlyHit = targetA.CurrentHp;
        var thirdHit = await WaitForNextDamagePacketAsync(stream, clock, WeakNoviceKnifeDelayMs);
        Assert.Equal(targetB.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(thirdHit.AsSpan(6)));
        Assert.Equal(targetAHpAfterItsOnlyHit, targetA.CurrentHp); // A took exactly its one hit, never a second.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class SequentialCellSelector(params (ushort X, ushort Y)[] cells) : IMobSpawnCellSelector
    {
        public bool TrySelectCell(MobSpawnDefinition spawn, int index, out MobPosition position)
        {
            position = new MobPosition(cells[index].X, cells[index].Y);
            return true;
        }
    }

    [Fact]
    public async Task Attack_DuplicateRepeatRequestsSameTarget_DoNotCreateConcurrentAttackLoops()
    {
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 1);
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Absent, inventoryListPersistence, gameplayState: WeakFreshNovice(), timeProvider: clock, rollWeaponAtk: MinWeaponAtkRoll);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        // First, establish a repeat attack and let its immediate hit fully land (rescheduling the
        // loop for a real future delay) before sending the duplicate - this isolates the
        // invariant this test actually targets (a duplicate request arriving WHILE an attack
        // timer is already pending must not add a second concurrent loop/second immediate hit,
        // pinned unit_attack's "just change target/type" guard, unit.cpp:2951-2953) from an
        // unrelated race: two requests sent back-to-back with no synchronization between them can
        // both legitimately be processed before either one's immediate hit executes (Athena's
        // packet-loop and repeat-attack-loop are independently scheduled async tasks, unlike
        // pinned rAthena's single-threaded server, where a second unit_attack call for the same
        // unit cannot even begin until the first one's synchronous unit_attack_timer call already
        // returned) - two genuinely-simultaneous FRESH requests can validly produce two immediate
        // hits, and this test must not assert otherwise.
        await stream.WriteAsync(AttackPacket(actorId));
        var (firstHit, _) = await ReadDamageAndHpInfoAsync(stream);
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(firstHit.AsSpan(6)));
        Assert.True(target.IsAlive, "WeakFreshNovice's Knife hit must not one-shot G_PORING for this test to observe the no-double-hit invariant.");

        // Now duplicate the SAME request while that attack timer is genuinely pending (confirmed
        // above) - this must not add a second concurrent loop or force an immediate second hit.
        await stream.WriteAsync(AttackPacket(actorId));

        // No second hit must already be queued on the wire immediately - only after a full delay.
        // No real-time wait is needed: the fake clock never advances on its own, so nothing server
        // -side can produce more bytes without either a clock advance or another client request -
        // a ping round-trip is enough to prove the server has caught up with everything sent so far.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));
        Assert.Equal(0, client.Available);

        var secondHit = await WaitForNextDamagePacketAsync(stream, clock, WeakNoviceKnifeDelayMs);
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(secondHit.AsSpan(6)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
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

    // Pinned unit_walktoxy (unit.cpp:888) unconditionally calls unit_stop_attack before starting
    // any walk - a real client movement request must cancel an active server-owned repeat attack,
    // not merely leave it to expire. Proven end-to-end: start a repeat attack, move away before
    // its next scheduled hit, then advance the fake clock generously and confirm no further
    // damage/vanish traffic ever arrives - only the movement response itself.
    [Fact]
    public async Task Attack_MovementRequest_CancelsActiveRepeatAttack()
    {
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 1);
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Absent, inventoryListPersistence, gameplayState: WeakFreshNovice(), timeProvider: clock, rollWeaponAtk: MinWeaponAtkRoll);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        await stream.WriteAsync(AttackPacket(actorId));
        await ReadDamageAndHpInfoAsync(stream); // Immediate first hit.
        Assert.True(target.IsAlive, "WeakFreshNovice's Knife hit must not one-shot G_PORING so a repeat state remains scheduled to be cancelled.");

        // Move away before the next scheduled hit - must cancel the repeat attack outright.
        await stream.WriteAsync(MovementRequestPacket(76, 51));
        var movementResponse = await ReadExact(stream, 12);
        Assert.Equal((short)PacketConstants.ZcNotifyPlayerMove, BinaryPrimitives.ReadInt16LittleEndian(movementResponse));

        // Advance well past the interval the cancelled attack would have fired at, then confirm
        // no damage/vanish packet ever arrives - only a harmless ping response.
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(WeakNoviceKnifeDelayMs * 5));
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));
        Assert.True(target.IsAlive); // Never took a second hit.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attack_SessionDisposal_CancelsOutstandingRepeatState_NoOrphanTask()
    {
        var inventoryPersistence = new RecordingInventoryPersistence(existingRowCount: 1);
        var knifeInventory = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var inventoryListPersistence = new FixedInventoryListPersistence(knifeInventory);
        var clock = new ControllableTimeProvider();
        var (client, stream, session, run, target) = await SetupAsync(inventoryPersistence, CharacterQuestStatus.Absent, inventoryListPersistence, gameplayState: WeakFreshNovice(), timeProvider: clock, rollWeaponAtk: MinWeaponAtkRoll);
        using var _ = client;

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ConsumeSelfWeaponAndSingleEquipInventoryBurst(stream);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        await stream.WriteAsync(AttackPacket(actorId));
        await ReadDamageAndHpInfoAsync(stream);
        Assert.True(target.IsAlive, "WeakFreshNovice's Knife hit must not one-shot G_PORING so a repeat state remains scheduled at disposal.");

        // Close the client (the same graceful-EOF shutdown path every other test in this file
        // uses) while a repeat attack is still scheduled (never fired again), then explicitly
        // dispose the session - StopAsync is idempotent and shared (MapClientSession's own doc
        // comment), so this either performs or joins the exact same shutdown RunAsync's own
        // `finally` already triggered, and must join the attack loop like every other background
        // loop, leaving no orphan Task/timer that could keep the test process alive.
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
