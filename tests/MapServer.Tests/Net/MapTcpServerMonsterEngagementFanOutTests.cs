using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.GameData.Quests;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Live-bug regression: MapTcpServer.RunMonsterTickLoopAsync called
// MonsterEngagementTickProcessor.ProcessAsync but DISCARDED its returned MonsterEngagementTickResult
// entirely - the early-out only checked MonsterRuntime's own `changed`/`respawned` counts, and the
// fan-out loop only ever sent THOSE two sources through NotifyMonsterMovedAsync. Every
// combat-driven MovementChange (chase start/retarget/fixpos) and every AttackActionOutcome (0x08C8)
// the processor computed was silently dropped - the processor's own diagnostics logged
// "outcome=WalkStarted"/"outcome=AttackAccepted" but no client ever actually received a packet for
// either.
//
// Every existing MonsterEngagementTickProcessor test (MapClientSessionMobEngagementTests) called
// ProcessAsync directly and then manually fanned the result out itself (that file's own
// FanOutAsync helper) - which proved the PROCESSOR's own outcome was correct, but never exercised
// MapTcpServer's own production tick-loop/fan-out code at all, so this exact bug could (and did)
// ship unnoticed.
//
// This file exercises MapTcpServer.ProcessOneMonsterTickAsync directly - the exact per-tick body
// RunMonsterTickLoopAsync's own loop calls, taking `sessions` as an explicit parameter (never a
// test-only production API for session membership - see that method's own doc comment). No
// packet-building or fan-out logic is duplicated here; every assertion reads real bytes off a real
// loopback socket that MapTcpServer's own NotifyMonsterMovedAsync/NotifyMonsterAttackOutcomeAsync
// calls wrote.
public sealed class MapTcpServerMonsterEngagementFanOutTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;
    private const uint OtherAccountId = 8;
    private const uint OtherCharId = 10;
    private const string Map = "int_land03";

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

    private static CharacterGameplayState FreshNovice(uint charId = CharId, uint hp = 40) => new(
        CharacterId: charId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: hp, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static async Task ConsumeBootstrapAsync(Stream stream)
    {
        await ReadExact(stream, 4 + 6 + 6 + 13); // 0x0B18/0x0283/0x0ADE/0x02EB.
        var skillListHeader = await ReadExact(stream, 4); // 0x0B32 header
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4); // 0x0B32 body
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa }); // 0x007D map-loaded.
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        await ReadExact(stream, 4);  // 0x0B0B inventoryEnd (unarmed, empty inventory - no item-list packets)
    }

    private static async Task ConsumeVisibleMonsterSpawnsAsync(Stream stream, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var header = await ReadExact(stream, 4);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            await ReadExact(stream, length - 4);
        }
    }

    private sealed record TestSession(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask);

    private sealed record TestWorld(MapTcpServer Server, MonsterRegistry Registry, MonsterCombatCoordinator Combat, MobInstance Poring, IMapCollisionProvider Collision, IMovementPathProvider PathProvider, MonsterSpatialInspector SpatialInspector, MonsterRuntime MonsterRuntime, WorldMapRegistry Maps, CharServerConnector CharConnector);

    // A real, fully-walkable, self-contained world (one G_PORING spawn) with a real MapTcpServer
    // built directly on top of it - same component shapes MapServerWorld.Build composes in
    // production, just without needing the full generated live world.
    private static TestWorld MakeWorld(ushort monsterX, ushort monsterY, TimeProvider timeProvider)
    {
        var allocator = new WorldActorIdAllocator();
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, Map, 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(monsterX, monsterY), timeProvider);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules());
        var collisionMap = new MapCollisionMap(Map, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray());
        IMapCollisionProvider collision = new MapCollisionProvider([collisionMap]);
        IMovementPathProvider pathProvider = new RathenaCompatibleMovementPathProvider(collision);
        var spatialInspector = new MonsterSpatialInspector(registry, collision);
        var monsterRuntime = new MonsterRuntime(registry, collision, pathProvider, timeProvider);
        var maps = WorldMapRegistry.Tutorial;
        var world = new MapServerWorld(maps, registry, combat, collision, spatialInspector, pathProvider, monsterRuntime);

        var charConnector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var configStore = new MapConfigStore(new MapConfig { MapPort = 0 }, "unused.conf");
        var server = new MapTcpServer(configStore, charConnector, world, timeProvider);

        return new TestWorld(server, registry, combat, registry.AllInstances[0], collision, pathProvider, spatialInspector, monsterRuntime, maps, charConnector);
    }

    private async Task<TestSession> ConnectSessionAsync(
        TestWorld world, uint accountId, uint charId, ushort x, ushort y, CharacterGameplayState? gameplayState,
        int visibleMonsterCount, TimeProvider timeProvider)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var gameplayPersistence = new RecordingGameplayStatePersistence(gameplayState ?? FreshNovice(charId));
        var session = new MapClientSession(
            (int)accountId, serverClient, world.CharConnector, true,
            Map, x, y, world.Maps,
            gameplayStatePersistence: gameplayPersistence,
            accountId: accountId, charId: charId, monsters: world.Registry, combat: world.Combat,
            timeProvider: timeProvider, collisionProvider: world.Collision, movementPathProvider: world.PathProvider,
            spatialInspector: world.SpatialInspector, monsterRuntime: world.MonsterRuntime);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(accountId, charId, 1, 2, 0, 0, false, Map, x, y, 0, 0, 0));

        await ConsumeBootstrapAsync(stream);
        await ConsumeVisibleMonsterSpawnsAsync(stream, visibleMonsterCount);

        return new TestSession(client, stream, session, run);
    }

    // The exact discriminating regression for the bug just fixed: runtime idle-walk AI produces
    // NOTHING this tick (the mob is engaged, so MonsterRuntime.ProcessTick's own HasActiveTarget
    // guard suppresses it) and nothing respawns - the ONLY reason this tick has anything to send at
    // all is engagementResult.MovementChanges containing a fresh chase WalkStarted. Before this
    // fix, MapTcpServer's old `if (changed.Count == 0 && respawned.Count == 0) continue;` would
    // discard this exact outcome and the victim would receive NOTHING - this test proves the victim
    // now receives the real 0x09FD bytes.
    [Fact]
    public async Task ProcessOneMonsterTickAsync_EngagementOnlyChaseStart_FansOutReal0x09FDToTheVictim()
    {
        var clock = new ControllableTimeProvider();
        var world = MakeWorld(monsterX: 85, monsterY: 51, clock);
        var victim = await ConnectSessionAsync(world, AccountId, CharId, 75, 51, FreshNovice(hp: 40), visibleMonsterCount: 1, clock);
        using var _dispose = victim.Client;

        // Real production combat path acquires the target - the mob is now engaged but has not
        // moved or attacked yet, so MonsterRuntime's own idle-walk AI is suppressed
        // (HasActiveTarget guard) and nothing has respawned.
        var attackOutcome = world.Combat.Attack(world.Poring, AccountId, new(9, 9, 9, 9, 9, 9, 0, 0), 1, null, _ => CharacterQuestStatus.Absent);
        Assert.True(attackOutcome.Accepted);
        Assert.True(world.Poring.HasActiveTarget);

        await world.Server.ProcessOneMonsterTickAsync([victim.Session], CancellationToken.None);

        Assert.True(world.Poring.IsWalking, "The Chase decision must have actually started server-owned movement.");

        // The victim must receive a REAL 0x09FD walk-entry packet through the actual production
        // fan-out - not merely a processor-internal outcome.
        var walkHeader = await ReadExact(victim.Stream, 4);
        var walkLength = BinaryPrimitives.ReadUInt16LittleEndian(walkHeader.AsSpan(2));
        var walkBody = await ReadExact(victim.Stream, walkLength - 4);
        Assert.Equal((short)PacketConstants.ZcNotifyMoveEntry, BinaryPrimitives.ReadInt16LittleEndian(walkHeader));
        // Actor ID sits at absolute offset 5 (clif.cpp's own PACKET_ZC_NOTIFY_MOVEENTRY layout: 2
        // bytes opcode + 2 bytes length + 1 byte objecttype, THEN AID.L) - offset 1 within
        // `walkBody`, which starts right after the 4-byte opcode+length header already consumed
        // above.
        var walkActorId = BinaryPrimitives.ReadUInt32LittleEndian(walkBody.AsSpan(1));
        Assert.Equal(world.Poring.ActorId, walkActorId);

        victim.Client.Close();
        await victim.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Independent discriminating regression for the attack side: runtime/respawn produce NOTHING
    // this tick, and the ONLY reason to send anything is engagementResult.AttackActions containing
    // a real attack outcome. Proves a future fix that only restores movement fan-out (but still
    // drops AttackActions) would still fail this test - both halves of the dropped result are
    // independently covered.
    [Fact]
    public async Task ProcessOneMonsterTickAsync_EngagementOnlyAttackOutcome_FansOutReal0x08C8ToTheVictim()
    {
        var clock = new ControllableTimeProvider();
        var world = MakeWorld(monsterX: 76, monsterY: 51, clock);
        // A stronger Attack than G_PORING's real value so this test observes a real nonzero-damage
        // hit deterministically - the ORCHESTRATION under test (ProcessOneMonsterTickAsync's own
        // fan-out) is unaffected by which Attack value the mob_db row happens to carry (see
        // MapClientSessionMobEngagementTests' own identical convention/comment for this fixture).
        var strongMobSpawn = world.Poring.Spawn with { Mob = world.Poring.Spawn.Mob with { Attack = 1000, AttackDelay = 2000 } };
        var strongRegistry = new MonsterRegistry([strongMobSpawn], new WorldActorIdAllocator().Allocate, new FixedCellSelector(76, 51), clock);
        var strongPoring = strongRegistry.AllInstances[0];
        var strongWorld = world with { Registry = strongRegistry, Poring = strongPoring };
        var strongServer = MakeServerFor(strongWorld, clock);
        strongWorld = strongWorld with { Server = strongServer };

        var victim = await ConnectSessionAsync(strongWorld, AccountId, CharId, 75, 51, FreshNovice(hp: 10_000), visibleMonsterCount: 1, clock);
        using var _dispose = victim.Client;

        strongPoring.TryAcquireTarget(AccountId, mode: MobMode.None); // Already in range - the very next tick decides Attack.

        await strongServer.ProcessOneMonsterTickAsync([victim.Session], CancellationToken.None);

        // The victim must receive a REAL 0x08C8 combat-action packet through the actual production
        // fan-out - not merely a processor-internal outcome.
        var damagePacket = await ReadExact(victim.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        var srcActorId = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(2));
        Assert.Equal(strongWorld.Poring.ActorId, srcActorId);
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
        Assert.True(damage > 0);

        victim.Client.Close();
        await victim.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static MapTcpServer MakeServerFor(TestWorld world, TimeProvider timeProvider)
    {
        var mapServerWorld = new MapServerWorld(world.Maps, world.Registry, world.Combat, world.Collision, world.SpatialInspector, world.PathProvider, world.MonsterRuntime);
        var configStore = new MapConfigStore(new MapConfig { MapPort = 0 }, "unused.conf");
        return new MapTcpServer(configStore, world.CharConnector, mapServerWorld, timeProvider);
    }
}
