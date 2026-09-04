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

// Step 6 hardening, item 8: a non-lethal local hit's own World-side confirmation
// (NotifyMonsterAttackedAsync) must be FAILED CLOSED - a non-success status (StaleLifeReference,
// StaleAttackerPresence, MonsterNotAttackable, AttackerNotEngageable) must produce NO wire-visible
// successful-hit packet and must clear the repeat-attack target, mirroring the existing lethal
// StaleLifeReference handling. Built as its own minimal wiring (mirroring
// MapClientSessionMonsterCombatTests' own manually-constructed-session tests, e.g.
// Attack_NewTargetRequest_ReplacesPriorRepeatTarget) rather than reusing that file's shared
// SetupAsync helper, since this test needs to script FakeCombatWorldRuntime's own
// NotifyMonsterAttackedStatusOverride - not exposed by SetupAsync's own return shape.
public sealed class MapClientSessionNonLethalAttackFailClosedTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

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

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    // WeakFreshNovice-shaped: guaranteed non-lethal against G_PORING's 55 HP unarmed, matching
    // MapClientSessionMonsterCombatTests' own established fixture for exactly this reason.
    private static CharacterGameplayState WeakFreshNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    private sealed class RecordingGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    [Theory]
    [InlineData(WorldMonsterAttackedStatus.StaleLifeReference)]
    [InlineData(WorldMonsterAttackedStatus.StaleAttackerPresence)]
    [InlineData(WorldMonsterAttackedStatus.MonsterNotAttackable)]
    [InlineData(WorldMonsterAttackedStatus.AttackerNotEngageable)]
    public async Task NonLethalHit_NotifyMonsterAttackedRejected_NoWireSuccessPacket_ClearsRepeatAttackTarget(WorldMonsterAttackedStatus rejectedStatus)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();
        using var disposableClient = client;

        var allocator = new WorldActorIdAllocator();
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
        var questDrops = new QuestDropResolver([]);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        var combatState = new MonsterCombatStateStore();
        combatState.Register(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value), target.Spawn.Mob.MaxHp);
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(), combatState);
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances);

        var gameplayPersistence = new RecordingGameplayStatePersistence(WeakFreshNovice());
        var fakeWorld = new FakeCombatWorldRuntime { NotifyMonsterAttackedStatusOverride = rejectedStatus };

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

        await stream.WriteAsync(AttackPacket(actorId));

        // The actual hit executes asynchronously on the session's own background repeat-attack loop
        // (HandleIroAttackRequestAsync itself only registers the repeat-attack target and returns) -
        // poll for the resulting combat-state key removal (the concrete, unambiguous side effect of
        // the fail-closed handling under test) with a bounded wait rather than assuming any fixed
        // number of packet round-trips already means the hit has landed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var key = new MonsterCombatKey(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value));
        while (combatState.TryGet(key, out _) && DateTime.UtcNow < deadline) await Task.Delay(20);

        // No damage/HP-info packet must ever arrive for this hit - the World-side rejection means
        // this must NOT be treated as a successful, wire-visible attack at all. Confirmed by
        // observing a harmless ping response land next instead of any combat packet.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        // The already-applied LOCAL HP mutation from MonsterCombatCoordinator.AttackAsync itself is
        // not rolled back (no distributed rollback protocol exists) - but the now-known-stale local
        // combat-state key must be discarded (mirroring the existing lethal StaleLifeReference
        // handling's own _combatState.Remove call), so a later stale read can never resurface it.
        Assert.False(combatState.TryGet(key, out _), "Expected the local combat-state key to be discarded after a rejected NotifyMonsterAttackedAsync result.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
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
