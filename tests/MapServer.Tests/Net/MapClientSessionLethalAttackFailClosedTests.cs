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

// Step 7 substep 9: the required lethal ordering is "CalculateAttack (read-only) -> resolve quest
// snapshot (read-only) -> allocate PendingMonsterDamageAttempt -> ApplyMonsterDamageAsync -> ONLY
// Applied/ReplayedSequence with KilledByThisHit triggers the wire/reward tail". A
// StaleLifeReference/AlreadyDead (or any other non-lethal-accepted) result must leave no
// damage/HP-info/death-vanish/reward packet, and must apply the exact per-status cadence-store-key
// disposition the substep-9 plan specifies: StaleLifeReference explicitly removes the local
// MonsterAttackCadenceStore key; AlreadyDead does NOT.
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

    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MonsterAttackCadenceStore CombatState, FakeCombatWorldRuntime FakeWorld, string MapId, WorldSimulationEpoch Epoch, uint ActorId, WorldMonsterIncarnationId Incarnation)> SetupAsync(WorldMonsterDamageStatus overrideStatus)
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
        // 1 HP so the very first deterministic hit is unconditionally lethal (were the RPC to accept it).
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
        var questDrops = new QuestDropResolver([]);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        var combatState = new MonsterAttackCadenceStore();
        var incarnation = new WorldMonsterIncarnationId(target.IncarnationId.Value);
        combatState.Register(target.Map, epoch, target.ActorId, incarnation);
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules());
        var fakeWorld = new FakeCombatWorldRuntime
        {
            ApplyMonsterDamageResultOverride = new WorldMonsterDamageResult(overrideStatus, 0, 0, 0, false, null),
        };
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances, fakeWorld);

        var gameplayPersistence = new RecordingGameplayStatePersistence(StrongAttacker());

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsterProjections: monsterProjections, combat: combat,
            combatState: combatState, distributedWorld: fakeWorld);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land", 75, 51, 0, 0, 0));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream); // 0x0B32 skill list

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        await ReadExact(stream, 4);  // 0x0B0B inventoryEnd
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        return (client, stream, session, run, combatState, fakeWorld, target.Map, epoch, actorId, incarnation);
    }

    [Fact]
    public async Task LethalHit_ApplyMonsterDamageRejectsWithStaleLifeReference_NoDamageNoHpInfoNoDeathVanishNoReward_CadenceKeyDiscarded()
    {
        var (client, stream, _, run, combatState, _, mapId, epoch, actorId, incarnation) = await SetupAsync(WorldMonsterDamageStatus.StaleLifeReference);
        using var disposableClient = client;

        await stream.WriteAsync(AttackPacket(actorId));

        // Live-acceptance wire-fidelity fix: pinned unit_attack's own due-now branch (unit.cpp:
        // 2971-2978) sends clif_fixpos (0x0088, the ATTACKER's own current position)
        // unconditionally, before the attack-timer-equivalent execution/World confirmation check -
        // sent regardless of whether World subsequently accepts or rejects the hit.
        var fixposPacket = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(fixposPacket.AsSpan(2)));

        // Poll for the local cadence-store key being discarded (StaleLifeReference's own observable
        // completion signal) with a bounded wait, rather than assuming a fixed number of packet
        // round-trips already means the hit was processed.
        var key = new MonsterCombatKey(mapId, epoch, actorId, incarnation);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (combatState.TryGet(key, out _) && DateTime.UtcNow < deadline) await Task.Delay(20);

        // No damage/HP-info/death-vanish/reward packet must EVER arrive for this hit - World's own
        // rejection happened before any wire/reward projection. Confirmed by observing a harmless
        // ping response land next instead of any combat packet.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        // StaleLifeReference proves the monster life itself is stale - the cadence-store key must be
        // explicitly discarded so a later stale read can never resurface it (§2 of the substep-9 plan).
        Assert.False(combatState.TryGet(key, out _), "Expected the local cadence-store key to be discarded after a StaleLifeReference rejection.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LethalHit_ApplyMonsterDamageRejectsWithAlreadyDead_CadenceKeySurvives_NoReward()
    {
        var (client, stream, _, run, combatState, _, mapId, epoch, actorId, incarnation) = await SetupAsync(WorldMonsterDamageStatus.AlreadyDead);
        using var disposableClient = client;

        var key = new MonsterCombatKey(mapId, epoch, actorId, incarnation);
        Assert.True(combatState.TryGet(key, out _));

        await stream.WriteAsync(AttackPacket(actorId));

        // Live-acceptance wire-fidelity fix: the due-now fixpos precedes even a World-rejected hit -
        // pinned unit_attack sends it unconditionally before the range/execution check.
        var fixposPacket = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(fixposPacket.AsSpan(2)));

        // No damage/HP-info/death-vanish/reward packet must EVER arrive - AlreadyDead means another
        // attacker (or an earlier uncounted commit) already won. Confirmed by observing a harmless
        // ping response land next.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        // AlreadyDead does NOT warrant discarding the cadence-store key - deliberately different from
        // StaleLifeReference (§2 of the substep-9 plan).
        Assert.True(combatState.TryGet(key, out _), "Expected the local cadence-store key to SURVIVE an AlreadyDead rejection.");

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
