using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 6 hardening (final correctness pass, item 1): the REQUIRED lethal ordering is
// "CalculateAttack (read-only) -> TryMarkMonsterDeadAsync -> ONLY MarkedDead -> atomically finalize
// local combat death (CommitConfirmedDeath) -> resolve quest/drop state -> 0x08C8 -> 0x0977 hp=0 ->
// EXP/drop persistence -> 0x0080 died". World's death confirmation is now obtained BEFORE any local
// combat-state mutation happens AT ALL - not merely before the wire/reward projection. A
// StaleLifeReference/AlreadyDead (or any non-MarkedDead) result from TryMarkMonsterDeadAsync must
// leave local HP COMPLETELY UNTOUCHED and produce none of the successful lethal wire/reward effects.
// Built as its own minimal wiring mirroring MapClientSessionNonLethalAttackFailClosedTests.cs's own
// established pattern (World-projection-based target, a scripted FakeCombatWorldRuntime) rather than
// that file's own MobInstance-based SetupAsync helper (MapClientSessionMonsterCombatTests.cs), since
// this needs to script TryMarkMonsterDeadStatusOverride specifically.
public sealed class MapClientSessionLethalAttackFailClosedTests
{
    private const uint AccountId = 11;
    private const uint CharId = 13;

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
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return buffer;
    }

    // A strong attacker guarantees this project's own deterministic (no-RNG, unarmed) statusAtk
    // formula one-shots a 1-HP target on the very first hit - see
    // MapTcpServerMonsterAuthorityIntegrationTests.cs's own doc comment for why an unarmed hit's
    // damage is fully deterministic (WeaponAttackCalculator's own pinned trace: weaponAtk is
    // hard-fixed at 0 for `weapon is null`, so only the deterministic STR/DEX/LUK/BaseLevel-derived
    // statusAtk applies).
    private static CharacterGameplayState StrongAttacker() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 99, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 100, CurrentSp: 10, MaxHp: 100, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 99, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 99, Luck: 9);

    private sealed class RecordingGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MonsterCombatStateStore CombatState, string MapId, WorldSimulationEpoch Epoch, uint ActorId, WorldMonsterIncarnationId Incarnation)> SetupAsync(WorldMonsterDeathStatus overrideStatus)
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
        // 1 HP so the very first deterministic hit is unconditionally lethal.
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
        var questDrops = new QuestDropResolver([]);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        var combatState = new MonsterCombatStateStore();
        var incarnation = new WorldMonsterIncarnationId(target.IncarnationId.Value);
        combatState.Register(target.Map, epoch, target.ActorId, incarnation, maxHp: 1); // 1 HP - guaranteed lethal.
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(), combatState);
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances);

        var gameplayPersistence = new RecordingGameplayStatePersistence(StrongAttacker());
        var fakeWorld = new FakeCombatWorldRuntime { TryMarkMonsterDeadStatusOverride = overrideStatus };

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsterProjections: monsterProjections, combat: combat,
            combatState: combatState, distributedWorld: fakeWorld);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream); // 0x0B32 skill list

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        await ReadExact(stream, 4);  // 0x0B0B inventoryEnd
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        return (client, stream, session, run, combatState, target.Map, epoch, actorId, incarnation);
    }

    [Fact]
    public async Task LethalHit_TryMarkMonsterDeadRejectsWithStaleLifeReference_NoLocalHpMutation_NoDamageNoHpInfoNoDeathVanishNoReward_KeyDiscarded()
    {
        var (client, stream, _, run, combatState, mapId, epoch, actorId, incarnation) = await SetupAsync(WorldMonsterDeathStatus.StaleLifeReference);
        using var disposableClient = client;

        await stream.WriteAsync(AttackPacket(actorId));

        // Poll for the local combat-state key being discarded (item 1's own observable completion
        // signal for a StaleLifeReference rejection) with a bounded wait, rather than assuming a
        // fixed number of packet round-trips already means the hit was processed.
        var key = new MonsterCombatKey(mapId, epoch, actorId, incarnation);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (combatState.TryGet(key, out _) && DateTime.UtcNow < deadline) await Task.Delay(20);

        // No damage/HP-info/death-vanish/reward packet must EVER arrive for this hit - World's own
        // rejection of the death confirmation happened BEFORE any local combat-state mutation, so
        // none of the successful lethal wire/reward effects may be projected. Confirmed by observing
        // a harmless ping response land next instead of any combat packet.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        // StaleLifeReference proves the monster life itself is stale - the combat-state key must be
        // discarded so a later stale read can never resurface it.
        Assert.False(combatState.TryGet(key, out _), "Expected the local combat-state key to be discarded after a StaleLifeReference death rejection.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LethalHit_TryMarkMonsterDeadRejectsWithAlreadyDead_LocalHpRemainsUntouched_KeySurvives_NoReward()
    {
        var (client, stream, _, run, combatState, mapId, epoch, actorId, incarnation) = await SetupAsync(WorldMonsterDeathStatus.AlreadyDead);
        using var disposableClient = client;

        var key = new MonsterCombatKey(mapId, epoch, actorId, incarnation);
        Assert.True(combatState.TryGet(key, out var before));
        Assert.Equal(1u, before.CurrentHp); // maxHp registered as 1 in SetupAsync.

        await stream.WriteAsync(AttackPacket(actorId));

        // No damage/HP-info/death-vanish/reward packet must EVER arrive - AlreadyDead is treated
        // conservatively as NOT proving this call owns a fresh death reward/projection (no
        // operation-identity mechanism exists to distinguish "replaying our own confirmed death"
        // from "racing a different attacker's own kill" - see PerformDueRepeatAttackAsync's own doc
        // comment). Confirmed by observing a harmless ping response land next.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        // AlreadyDead does NOT prove the monster life itself is stale (unlike StaleLifeReference) -
        // the combat-state key must SURVIVE, with HP COMPLETELY UNTOUCHED (never locally mutated to
        // 0 before World's confirmation, per this pass's own core fix).
        Assert.True(combatState.TryGet(key, out var after), "Expected the local combat-state key to SURVIVE an AlreadyDead rejection.");
        Assert.Equal(1u, after.CurrentHp);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
    {
        public bool TrySelectCell(MobSpawnDefinition spawn, int index, out MobPosition position)
        {
            position = new MobPosition(x, y);
            return true;
        }
    }
}
