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

// CHARACTERIZATION tests for "monsters appear on map entry, then flash/disappear shortly after".
//
// A reconnecting player's NEW MapClientSession discovers monsters at map load straight from the
// already-existing (retained) MonsterFeedProjection (SendVisibleMonsterActorsAsync) - a projection
// that may be STALE (MapServer only polls maps that have sessions, so its cursor/positions/HP date
// from when the previous session left). If MapTcpServer then applies a snapshot/resync page for
// that map, ReconcileMonsterVisibilityAsync runs for the new session with no recorded
// last-reconciled epoch, which MonsterVisibilityState deliberately reports as "epoch changed"
// (its documented first-reconciliation rule): every monster the session just discovered is vanished
// (0x0080 OutOfSight) and rediscovered from the FRESH snapshot - a visible appear -> vanish ->
// reappear flash at login for every monster still in view.
//
// This is pinned here as CURRENT, DELIBERATE behavior, NOT as a proven bug to "fix" by skipping the
// vanish: that vanish/rediscovery is also what corrects positions/HP the stale map-load scan showed.
// Making the first reconcile silent would leave stale sprites in place until each monster's next
// walk packet (an already-visible actor's CellCrossed/WalkFinished send nothing). The underlying
// defect - discovering from a stale projection at session join - needs a projection-freshness design
// (e.g. poll before the map-load scan), which is out of scope for this bugfix branch; see the
// investigation report. These tests exist so any future change to that behavior is a conscious one.
public sealed class MapClientSessionMonsterLoginVisibilityTests
{
    private const uint AccountId = 2_000_000;
    private const uint CharId = 1;

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
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

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private static async Task AssertNothingSentAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));
    }

    // Boots a real authenticated session on a map whose projection ALREADY holds one Poring inside
    // the player's AOI, and consumes the map-load handshake up to (and including) the monster's
    // map-load discovery packet.
    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task Run, MonsterFeedProjection Projection, WorldSimulationEpoch Epoch, MobInstance Monster, MonsterAttackCadenceStore CombatState)> SetupAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var fakeWorld = new FakeCombatWorldRuntime();
        var allocator = new WorldActorIdAllocator();
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(75, 51), TimeProvider.System);
        var monster = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        var combatState = new MonsterAttackCadenceStore();
        combatState.Register(monster.Map, epoch, monster.ActorId, new WorldMonsterIncarnationId(monster.IncarnationId.Value));
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules());
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(monster.Map, epoch, combatState, registry.AllInstances, fakeWorld);

        var state = new CharacterGameplayState(
            CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
            BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
            StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            accountId: AccountId, charId: CharId, monsterProjections: monsterProjections, combat: combat,
            combatState: combatState, distributedWorld: fakeWorld);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land", 75, 51, 0, 0, 0, CharacterName: "TestNovice"));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa }); // Client map-loaded packet.
        await ReadExact(stream, 15);
        await ReadExact(stream, 6);
        await ReadExact(stream, 4);

        // The map-load discovery scan (SendVisibleMonsterActorsAsync) announces the Poring from the
        // already-existing projection.
        var discovery = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(discovery));
        Assert.Equal(monster.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(discovery.AsSpan(5)));

        Assert.True(monsterProjections.TryGet("int_land", out var projection));
        return (client, stream, session, run, projection!, epoch, monster, combatState);
    }

    // The first snapshot page MapTcpServer applies for this map after the session joined - SAME
    // epoch, monster unchanged, alive, inside the AOI. Current behavior: exactly one OutOfSight
    // vanish followed by one 0x09FF rediscovery (the login flash), then silence. The vanish is
    // logged live as reason=OutOfSight-resync-epoch-changed feed=reconcile-snapshot.
    [Fact]
    public async Task FirstReconcile_SameEpoch_AfterMapLoadDiscovery_VanishesThenRediscovers_LoginFlashCharacterization()
    {
        var (client, stream, session, run, projection, _, monster, _) = await SetupAsync();
        using var _dispose = client;

        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);

        var vanish = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanish));
        Assert.Equal(monster.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(vanish.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonOutOfSight, vanish[6]);

        var rediscovery = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(rediscovery));
        Assert.Equal(monster.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(rediscovery.AsSpan(5)));

        // Once reconciled, a SECOND reconcile against the same epoch is silent: the flash happens
        // exactly once per session join, not on every snapshot page.
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);
        await AssertNothingSentAsync(stream);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // A genuinely different epoch after map-load discovery vanishes-then-rediscovers too (same
    // packets; the reason label differs only in the diagnostic log).
    [Fact]
    public async Task FirstReconcile_DifferentEpoch_AfterMapLoadDiscovery_VanishesThenRediscovers()
    {
        var (client, stream, session, run, projection, _, monster, combatState) = await SetupAsync();
        using var _dispose = client;

        var newEpoch = WorldSimulationEpoch.NewEpoch();
        var current = projection.AllInstances.Single();
        projection.ApplySnapshot([current], newEpoch, combatState);
        await session.ReconcileMonsterVisibilityAsync(projection, CancellationToken.None);

        var vanish = await ReadExact(stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanish));
        Assert.Equal(monster.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(vanish.AsSpan(2)));

        var rediscovery = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(rediscovery));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
