using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.World.PrtFild08;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Proves the travel-corridor's source-backed prt_fild08d field population (ai/world-data.md's
// "Travel corridor" section, izlude-prontera-travel-trace.txt) flows through the EXISTING generic
// visibility/combat/respawn pipeline exactly like every other generated map's monsters - no
// prt_fild08d-specific runtime code exists anywhere in this path. Uses
// PrtFild08MobSpawns.PrtFild08D (real generated data, ordinary Poring/1002 - never the
// tutorial-only G_PORING/2401) and
// MapClientSession's real socket path, the same pattern MapClientSessionMonsterCombatTests already
// uses for the Academy slice.
[CollectionDefinition(nameof(PrtFild08dMonsterVisibilityAndRespawnIntegrationTests), DisableParallelization = true)]
public sealed class PrtFild08dMonsterVisibilityAndRespawnIntegrationTestsCollection;

[Collection(nameof(PrtFild08dMonsterVisibilityAndRespawnIntegrationTests))]
public sealed class PrtFild08dMonsterVisibilityAndRespawnIntegrationTests
{
    private const uint AccountId = 71;
    private const uint CharId = 91;

    private sealed class NoOpQuestPersistence : ICharacterQuestPersistence
    {
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint questId, CancellationToken cancellationToken) =>
            Task.FromResult<CharacterQuestStatus?>(CharacterQuestStatus.Absent);
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus state, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) =>
            Task.FromResult<CharacterGameplayState?>(updated);
    }

    private sealed class NoOpInventoryPersistence : ICharacterInventoryPersistence
    {
        public Task<InventoryAddPersistenceResult> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken) =>
            Task.FromResult(new InventoryAddPersistenceResult(true, amount, 1, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0, IsNewRow: true));
        public Task<InventoryConsumePersistenceResult> ConsumeItemAsync(uint accountId, uint charId, uint durableId, uint amount, CancellationToken cancellationToken) =>
            Task.FromResult(InventoryConsumePersistenceResult.Failed());
    }

    // Strong enough to kill ordinary Poring's 55 HP in very few hits - same rationale as
    // MapClientSessionMonsterCombatTests.StrongNovice.
    private static CharacterGameplayState StrongNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 99, JobLevel: 10,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 100, CurrentSp: 100, MaxHp: 100, MaxSp: 100,
        StatPoints: 0, SkillPoints: 0, Strength: 99, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 99, Luck: 99);

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

    [Fact]
    public async Task PlayerEnteringPrtFild08d_DiscoversSourceBackedPoring_ThroughNormalVisibilityPipeline_ThenKillsAndRespawnsItThroughTheGenericPipeline()
    {
        // Real generated spawn declaration (source-backed: legacy/rathena/npc/re/mobs/academy.txt,
        // ordinary Poring/1002, count 110, delay 5000 for prt_fild08d - ai/world-data.md), not a
        // hand-authored test fixture. Only the CELL SELECTOR is a test double (deterministic
        // placement), matching every other MapClientSession integration test's own convention.
        var spawn = PrtFild08MobSpawns.PrtFild08D.Single(s => s.Mob == GeneratedMobs.Poring);
        Assert.Same(GeneratedMobs.Poring, spawn.Mob);
        Assert.Equal(110, spawn.Count);
        Assert.Equal(5000, spawn.RespawnDelayMs);

        var clock = new FakeTimeProvider();
        var allocator = new WorldActorIdAllocator();
        var registry = new MonsterRegistry([spawn with { Count = 1 }], allocator, new FixedCellSelector(500, 500), clock);
        var combat = new MonsterCombatCoordinator(registry, new QuestDropResolver(Generated.GameData.Quests.GeneratedQuestDrops.All), new RenewalBasicAttackRules());
        var target = registry.AllInstances[0];
        Assert.Equal("prt_fild08d", target.Map);
        Assert.Equal(GeneratedMobs.Poring.Id, target.Spawn.Mob.Id);
        Assert.NotEqual(2401, target.Spawn.Mob.Id); // Real ordinary Poring (1002), never the tutorial G_PORING (2401).

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
            "prt_fild08d", 500, 500, WorldMapRegistry.Tutorial,
            questPersistence: new NoOpQuestPersistence(), gameplayStatePersistence: new FixedGameplayStatePersistence(StrongNovice()),
            accountId: AccountId, charId: CharId, monsters: registry, combat: combat,
            inventoryPersistence: new NoOpInventoryPersistence());
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "prt_fild08d", 500, 500, 0, 0, 0));

        // Fixed 4-packet iRO bootstrap (0x0B18/0x0283/0x0ADE/0x02EB) plus the variable-length
        // 0x0B32 skill list that always follows it - same as every other MapClientSession test.
        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream);

        // --- Discover through the NORMAL visibility pipeline (0x007D map-loaded -> 0x09FF spawn) ---
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6); // 0x0B08 inventoryStart
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd
        var spawnPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(spawnPacket));
        Assert.Equal((byte)5, spawnPacket[4]); // objecttype=5 (NPC_MOB_TYPE)
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawnPacket.AsSpan(5));
        Assert.Equal(target.ActorId, actorId);
        Assert.Equal((ushort)1002, BinaryPrimitives.ReadUInt16LittleEndian(spawnPacket.AsSpan(23))); // real Poring class, not G_PORING's 2401.

        // --- Kill through the EXISTING generic combat pipeline (same wire path as G_PORING) ---
        // Unlike G_PORING (BaseExp/JobExp both 0), ordinary Poring's real pinned EXP (150/40) makes
        // the kill also emit a variable progression-packet sequence (0x0ACB/0x0ACC/0x00B0/0x019B,
        // exactly as Attack_SourceBackedNonzeroMob_AppliesRatesPersistsThenSendsProgressionAndVisuals
        // already proves for this same generic pipeline) before the terminal 0x0080 vanish - drained
        // generically here rather than assumed away, since this test's point is exercising the real
        // pipeline, not re-deriving G_PORING's simplified zero-EXP shape.
        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            await stream.WriteAsync(AttackPacket(actorId));
            var damagePacket = await ReadExact(stream, PacketConstants.ZcNotifyAct3Length);
            Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
            Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(6)));
            Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22)) > 0, "Expected the strong test attacker to deal nonzero damage.");

            if (!target.IsAlive)
            {
                while (true)
                {
                    var header = await ReadExact(stream, 2);
                    var id = BinaryPrimitives.ReadInt16LittleEndian(header);
                    if (id == PacketConstants.ZcNotifyVanish)
                    {
                        var rest = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength - 2);
                        var vanish = header.Concat(rest).ToArray();
                        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(vanish.AsSpan(2)));
                        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanish[6]);
                        break;
                    }
                    var length = id switch
                    {
                        PacketConstants.ZcLongLongParameterChange => 12,
                        PacketConstants.ZcNotifyExperience => PacketConstants.ZcNotifyExperienceLength,
                        PacketConstants.ZcNotifyEffect => PacketConstants.ZcNotifyEffectLength,
                        PacketConstants.ZcParameterChange => 8,
                        _ => throw new InvalidDataException($"Unexpected post-death packet 0x{id:X4}."),
                    };
                    await ReadExact(stream, length - 2);
                }
            }
        }
        Assert.False(target.IsAlive);

        // --- Respawn through the EXISTING generic respawn pipeline (same as every other map) ---
        clock.Advance(TimeSpan.FromMilliseconds(target.Spawn.RespawnDelayMs + 1));
        var respawned = registry.ProcessDueRespawns();
        Assert.Single(respawned);
        Assert.Same(target, respawned[0]);
        Assert.True(target.IsAlive);
        Assert.Equal(target.Spawn.Mob.MaxHp, target.CurrentHp);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
