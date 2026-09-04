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

// Step 6 hardening (final correctness pass), item 2: a TRANSIENT World RPC failure
// (NotifyMonsterAttackedAsync for a non-lethal hit, TryMarkMonsterDeadAsync for a lethal hit) must
// not fault MapClientSession's own background repeat-attack loop task - it is caught, logged, local
// combat state is left completely untouched, the schedule is re-armed for a normal LATER attempt
// (never a tight retry loop), and the loop itself stays alive so that later attempt can actually run
// and succeed. Built as its own minimal wiring mirroring
// MapClientSessionNonLethalAttackFailClosedTests.cs/MapClientSessionLethalAttackFailClosedTests.cs's
// own established pattern.
public sealed class MapClientSessionTransientWorldRpcFailureTests
{
    private const uint AccountId = 41;
    private const uint CharId = 43;

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

    // WeakFreshNovice-shaped: guaranteed non-lethal against G_PORING's 55 HP unarmed.
    private static CharacterGameplayState WeakFreshNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    // A strong attacker guarantees this project's own deterministic (no-RNG, unarmed) statusAtk
    // formula one-shots a 1-HP target on the very first hit.
    private static CharacterGameplayState StrongAttacker() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 99, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 100, CurrentSp: 10, MaxHp: 100, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 99, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 99, Luck: 9);

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

    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MonsterCombatStateStore CombatState, FakeCombatWorldRuntime FakeWorld, string MapId, WorldSimulationEpoch Epoch, uint ActorId, WorldMonsterIncarnationId Incarnation)> SetupAsync(
        CharacterGameplayState attackerState, uint maxHp, FakeCombatWorldRuntime fakeWorld)
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
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
        var questDrops = new QuestDropResolver([]);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        var combatState = new MonsterCombatStateStore();
        var incarnation = new WorldMonsterIncarnationId(target.IncarnationId.Value);
        combatState.Register(target.Map, epoch, target.ActorId, incarnation, maxHp);
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(), combatState);
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances);

        var gameplayPersistence = new RecordingGameplayStatePersistence(attackerState);

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

        return (client, stream, session, run, combatState, fakeWorld, target.Map, epoch, actorId, incarnation);
    }

    [Fact]
    public async Task NonLethalHit_NotifyMonsterAttackedTransientFailure_HpUnchanged_LoopSurvives_LaterAttemptSucceeds()
    {
        var fakeWorld = new FakeCombatWorldRuntime { ThrowTransientNotifyMonsterAttackedCount = 1 };
        var (client, stream, _, run, combatState, world, mapId, epoch, actorId, incarnation) = await SetupAsync(WeakFreshNovice(), maxHp: 55, fakeWorld);
        using var disposableClient = client;

        var key = new MonsterCombatKey(mapId, epoch, actorId, incarnation);

        await stream.WriteAsync(AttackPacket(actorId));

        // Wait for the first (failing) attempt to be observed, then for the SUCCESSFUL retry to
        // actually mutate HP - the repeat-attack loop's own re-armed cadence must produce a second
        // attempt on its own, without any further client action.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && world.NotifyMonsterAttackedCallCount < 2) await Task.Delay(20);
        Assert.True(world.NotifyMonsterAttackedCallCount >= 2, "Expected the transient failure to be retried by a later scheduled attempt (the loop must not have died).");

        // Bounded wait for the eventual successful hit's own HP mutation to land.
        while (DateTime.UtcNow < deadline && combatState.TryGet(key, out var current) && current.CurrentHp == 55) await Task.Delay(20);

        Assert.True(combatState.TryGet(key, out var final));
        Assert.True(final.CurrentHp < 55, "Expected the later successful retry to eventually apply real damage.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LethalHit_TryMarkMonsterDeadTransientFailure_HpUnchanged_NoLethalPacketsOrRewards_LoopSurvives_LaterResolutionWorks()
    {
        var fakeWorld = new FakeCombatWorldRuntime { ThrowTransientTryMarkMonsterDeadCount = 1 };
        var (client, stream, _, run, combatState, world, mapId, epoch, actorId, incarnation) = await SetupAsync(StrongAttacker(), maxHp: 1, fakeWorld);
        using var disposableClient = client;

        var key = new MonsterCombatKey(mapId, epoch, actorId, incarnation);

        await stream.WriteAsync(AttackPacket(actorId));

        // The FIRST attempt hits the transient failure - local HP must remain untouched (item 1's
        // own "confirm before commit" ordering means the transient RPC failure happens BEFORE any
        // local combat-state mutation at all) and the loop must survive to make a SECOND attempt on
        // its own re-armed cadence, which this time reaches the fake's own real MarkedDead path and
        // actually confirms the death.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && world.TryMarkMonsterDeadCallCount < 2) await Task.Delay(20);
        Assert.True(world.TryMarkMonsterDeadCallCount >= 2, "Expected the transient failure to be retried by a later scheduled attempt (the loop must not have died).");

        // Bounded wait for the eventual successful confirmed-death commit to land.
        while (DateTime.UtcNow < deadline && combatState.TryGet(key, out var current) && current.CurrentHp != 0) await Task.Delay(20);

        Assert.True(combatState.TryGet(key, out var final));
        Assert.Equal(0u, final.CurrentHp); // Eventually confirmed and committed by the later successful retry.
        Assert.True(world.IsConfirmedDead(new WorldMonsterLifeReference(mapId, epoch, actorId, incarnation)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
