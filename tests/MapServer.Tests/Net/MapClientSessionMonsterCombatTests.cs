using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
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
    // deterministic without depending on the exact BasicAttackCalculator formula's per-hit value.
    private static CharacterGameplayState StrongNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 99, JobLevel: 10,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 100, CurrentSp: 100, MaxHp: 100, MaxSp: 100,
        StatPoints: 0, SkillPoints: 0, Strength: 99, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 99, Luck: 99);

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target)> SetupAsync(
        RecordingInventoryPersistence inventoryPersistence, CharacterQuestStatus questState)
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
        var combat = new MonsterCombatCoordinator(registry, questDrops);
        var target = registry.AllInstances[0];

        var questPersistence = new RecordingQuestPersistence(Quest21008, questState);
        var gameplayPersistence = new RecordingGameplayStatePersistence(StrongNovice());

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            questPersistence: questPersistence, gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsters: registry, combat: combat,
            inventoryPersistence: inventoryPersistence);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));

        // Consume the fixed 4-packet iRO bootstrap (0x0B18/0x0283/0x0ADE/0x02EB).
        await ReadExact(stream, 4 + 6 + 6 + 13);

        return (client, stream, session, run, target);
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
}
