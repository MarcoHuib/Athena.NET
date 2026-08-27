using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.GameData.Quests;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Live-bug regression (mob idle random walk continuing during combat engagement): proves the
// REAL production orchestration - MonsterEngagementTickProcessor, the exact type
// MapTcpServer.RunMonsterTickLoopAsync constructs and calls every tick - correctly wires
// MonsterEngagementDomain's decisions through to a real MapClientSession over the real wire
// protocol. This is deliberately NOT a hand-written reimplementation of the orchestration
// algorithm: every test below constructs the same MonsterEngagementTickProcessor production type
// and calls ProcessAsync, exactly like MapTcpServer does - a regression in session lookup, map
// filtering, outcome application, or ordering inside that processor would fail these tests.
//
// Production call chain this exercises: MapTcpServer.RunMonsterTickLoopAsync ->
// MonsterEngagementTickProcessor.ProcessAsync -> MonsterEngagementDomain.Evaluate -> (Chase:
// MobInstance.TryRetargetChase/TryStartChase/EnterChaseState) | (Attack: MobInstance.StopChase/
// EnterAttackState -> MapClientSession.TryGetCombatSnapshotAsync -> MobBasicAttackCalculator.
// Calculate -> MobInstance.ScheduleNextAttack -> MapClientSession.ApplyIncomingMobBasicAttackAsync
// -> real 0x08C8 damage + 0x00B0 SP_HP packets on the wire).
public sealed class MapClientSessionMobEngagementTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;
    private const uint OtherAccountId = 8;
    private const uint OtherCharId = 10;

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

    private static CharacterGameplayState FreshNovice(uint charId = CharId, ushort vit = 1, uint hp = 40) => new(
        CharacterId: charId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: hp, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: vit, Intelligence: 9, Dexterity: 9, Luck: 9);

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
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa }); // 0x007D map-loaded.
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        // Unarmed with an empty inventory: neither the normal-item nor equip-item list packet is
        // sent at all (IroInventoryListPackets' own batch-count guards) - inventoryEnd follows
        // directly.
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd
    }

    // Consumes every 0x09FF monster-stand-entry packet a session's own visibility range produces
    // at connect time, so each test's own subsequent reads start from a clean wire regardless of
    // how many spawned mobs happen to be within range of that session's starting position.
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

    private async Task<TestSession> ConnectSessionAsync(
        MonsterRegistry registry, MonsterCombatCoordinator combat, uint accountId, uint charId,
        ushort x, ushort y, string map, CharacterGameplayState? gameplayState, int visibleMonsterCount)
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
            (int)accountId, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            map, x, y, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: accountId, charId: charId, monsters: registry, combat: combat);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(accountId, charId, 1, 2, 0, 0, false, map, x, y, 0, 0, 0));

        await ConsumeBootstrapAsync(stream);
        await ConsumeVisibleMonsterSpawnsAsync(stream, visibleMonsterCount);

        return new TestSession(client, stream, session, run);
    }

    private sealed record TestWorld(MonsterRegistry Registry, MonsterCombatCoordinator Combat, MonsterEngagementTickProcessor Processor, MobInstance Poring, IMapCollisionProvider Collision, IMovementPathProvider PathProvider);

    // A real, fully-walkable MapCollisionMap-backed provider (not EmptyMapCollisionProvider, whose
    // own TryGetMap always returns false) - the Chase decision's fresh-walk fallback
    // (MonsterEngagementTickProcessor.ApplyChaseDecision) only ever calls ComputePath after its own
    // TryGetMap check succeeds, matching MonsterRuntimeTests' own real-collision-map fixture
    // pattern for the identical reason.
    private static TestWorld MakeWorld(ushort monsterX, ushort monsterY, string map = "int_land03", TimeProvider? timeProvider = null)
    {
        var allocator = new WorldActorIdAllocator();
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, map, 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator, new FixedCellSelector(monsterX, monsterY), TimeProvider.System);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules());
        var collisionMap = new MapCollisionMap(map, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray());
        IMapCollisionProvider collision = new MapCollisionProvider([collisionMap]);
        IMovementPathProvider pathProvider = new RathenaCompatibleMovementPathProvider(collision);
        var processor = new MonsterEngagementTickProcessor(registry, collision, pathProvider, timeProvider ?? TimeProvider.System);
        return new TestWorld(registry, combat, processor, registry.AllInstances[0], collision, pathProvider);
    }

    // Steps 1-5: passive Poring is attacked (acquires target), player is out of range, the
    // production processor runs, decision is Chase, and the mob's authoritative position actually
    // moves toward the player - the core observable fix for the reported "keeps running off"
    // behavior.
    [Fact]
    public async Task ProcessAsync_TargetOutOfRange_ChasesTowardThePlayer_UsingTheRealProductionProcessor()
    {
        var world = MakeWorld(monsterX: 85, monsterY: 51);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", null, visibleMonsterCount: 1);
        using var _dispose = player.Client;

        world.Combat.Attack(world.Poring, AccountId, new(9, 9, 9, 9, 9, 9, 0, 0), 1, null, _ => CharacterQuestStatus.Absent);
        Assert.True(world.Poring.HasActiveTarget); // Attack itself acquires the target (real production path).

        var before = world.Poring.GetPosition();
        await world.Processor.ProcessAsync([player.Session], CancellationToken.None);

        Assert.Equal(MobCombatState.Rush, world.Poring.Engagement.State);
        Assert.True(world.Poring.IsWalking, "Processor's Chase decision must actually start server-owned movement.");

        // Advance real elapsed time and confirm the mob is genuinely closer to the player, not
        // wandering to an unrelated destination.
        world.Poring.AdvanceMovement(DateTimeOffset.UtcNow.AddSeconds(2));
        var after = world.Poring.GetPosition();
        Assert.True(after.X < before.X, "Chase must move the mob toward the player's actual position.");

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Steps 6-13: drive the mob into range via the SAME processor, confirm the decision flips to
    // Attack, confirm real HP mutation + the correct stock-iRO packets arrive over the actual
    // client wire, confirm the attack cooldown withholds a second hit before the pinned deadline,
    // and confirm a later tick (once the deadline has passed) produces the next hit.
    [Fact]
    public async Task ProcessAsync_TargetInRange_AttacksThroughRealSession_ThenRespectsCooldown_ThenHitsAgain()
    {
        var clock = new ControllableTimeProvider();
        var world = MakeWorld(monsterX: 76, monsterY: 51, timeProvider: clock);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(vit: 1, hp: 10_000), visibleMonsterCount: 1);
        using var _dispose = player.Client;

        // G_PORING's real Attack=1 cannot clear even a Vitality=1 target's def2 under the pinned RE
        // DEF-reduction formula (see MobBasicAttackCalculatorTests' own finding) - substitute a
        // stronger Attack purely so this test can observe real nonzero damage on the wire; the
        // ORCHESTRATION under test (processor -> domain -> calculator -> session) is unaffected by
        // which Attack value the mob_db row happens to carry.
        var strongPoring = new MobInstance(world.Poring.ActorId, world.Poring.Spawn with { Mob = world.Poring.Spawn.Mob with { Attack = 1000, AttackDelay = 2000 } }, 76, 51);
        var strongRegistry = new MonsterRegistry([strongPoring.Spawn], new WorldActorIdAllocator(), new FixedCellSelector(76, 51), TimeProvider.System);
        var strongProcessor = new MonsterEngagementTickProcessor(strongRegistry, world.Collision, world.PathProvider, clock);
        var strongMob = strongRegistry.AllInstances[0];
        strongMob.TryAcquireTarget(AccountId, allowChangeTargetWhileChasing: false);

        await strongProcessor.ProcessAsync([player.Session], CancellationToken.None);
        Assert.Equal(MobCombatState.Berserk, strongMob.Engagement.State);

        var damagePacket = await ReadExact(player.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        Assert.Equal(strongMob.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(2)));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(6)));
        var firstDamage = BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(22));
        Assert.True(firstDamage > 0);

        var hpPacket = await ReadExact(player.Stream, 8);
        Assert.Equal((short)PacketConstants.ZcParameterChange, BinaryPrimitives.ReadInt16LittleEndian(hpPacket));
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(hpPacket.AsSpan(2))); // SP_HP.
        var hpAfterFirstHit = BinaryPrimitives.ReadUInt32LittleEndian(hpPacket.AsSpan(4));
        Assert.Equal(10_000u - firstDamage, hpAfterFirstHit);
        Assert.Equal(hpAfterFirstHit, player.Session.GameplayState!.State.CurrentHp);

        // Cooldown: immediately re-running the processor before AttackDelay (2000ms) elapses must
        // produce no further hit at all.
        await strongProcessor.ProcessAsync([player.Session], CancellationToken.None);
        await player.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(player.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));
        Assert.Equal(hpAfterFirstHit, player.Session.GameplayState!.State.CurrentHp); // Unchanged.

        // Advance to (and past) the mob's own scheduled next-attack deadline, then run the SAME
        // processor again - the next hit must now occur.
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(2001));
        await strongProcessor.ProcessAsync([player.Session], CancellationToken.None);

        var secondDamagePacket = await ReadExact(player.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(secondDamagePacket));
        var secondDamage = BinaryPrimitives.ReadUInt32LittleEndian(secondDamagePacket.AsSpan(22));
        Assert.True(secondDamage > 0);

        var secondHpPacket = await ReadExact(player.Stream, 8);
        var hpAfterSecondHit = BinaryPrimitives.ReadUInt32LittleEndian(secondHpPacket.AsSpan(4));
        Assert.Equal(hpAfterFirstHit - secondDamage, hpAfterSecondHit);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Requirement 7 / item 9: target session missing entirely (e.g. process restarted mid-tick,
    // or was never found) resolves to the domain's own source-backed Unlock, applied by the real
    // processor - never a thrown exception or a silently-stuck engagement.
    [Fact]
    public async Task ProcessAsync_TargetSessionMissing_UnlocksTheMobThroughTheRealProcessor()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51);
        world.Poring.TryAcquireTarget(AccountId, allowChangeTargetWhileChasing: false);

        await world.Processor.ProcessAsync([], CancellationToken.None); // No sessions at all.

        Assert.False(world.Poring.HasActiveTarget);
        Assert.Equal(MobCombatState.Idle, world.Poring.Engagement.State);
    }

    // Requirement 7: target session exists but has moved to a different map - the processor's own
    // snapshot correctly carries the CURRENT map, and the domain unlocks rather than attacking
    // across maps.
    [Fact]
    public async Task ProcessAsync_TargetOnADifferentMap_UnlocksThroughTheRealProcessor()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51, map: "int_land03");
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "iz_int03", null, visibleMonsterCount: 0);
        using var _dispose = player.Client;
        world.Poring.TryAcquireTarget(AccountId, allowChangeTargetWhileChasing: false);

        await world.Processor.ProcessAsync([player.Session], CancellationToken.None);

        Assert.False(world.Poring.HasActiveTarget);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Requirement 7: target player is dead (CurrentHp == 0) - the processor's snapshot reflects
    // that, and the domain unlocks rather than continuing to attack a dead target.
    [Fact]
    public async Task ProcessAsync_TargetPlayerIsDead_UnlocksThroughTheRealProcessor()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51);
        var player = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(hp: 0), visibleMonsterCount: 1);
        using var _dispose = player.Client;
        world.Poring.TryAcquireTarget(AccountId, allowChangeTargetWhileChasing: false);

        await world.Processor.ProcessAsync([player.Session], CancellationToken.None);

        Assert.False(world.Poring.HasActiveTarget);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Item 9: a second, unrelated player near the same engaged mob must never be damaged - the
    // processor looks up the session by the mob's OWN TargetAccountId, never "any nearby session".
    [Fact]
    public async Task ProcessAsync_SecondUnrelatedSession_IsNeverDamaged()
    {
        var world = MakeWorld(monsterX: 76, monsterY: 51);
        var target = await ConnectSessionAsync(world.Registry, world.Combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(CharId, hp: 40), visibleMonsterCount: 1);
        using var _disposeTarget = target.Client;
        var bystander = await ConnectSessionAsync(world.Registry, world.Combat, OtherAccountId, OtherCharId, 76, 52, "int_land03", FreshNovice(OtherCharId, hp: 40), visibleMonsterCount: 1);
        using var _disposeBystander = bystander.Client;

        var strongMobSpawn = world.Poring.Spawn with { Mob = world.Poring.Spawn.Mob with { Attack = 1000 } };
        var strongRegistry = new MonsterRegistry([strongMobSpawn], new WorldActorIdAllocator(), new FixedCellSelector(76, 51), TimeProvider.System);
        var strongProcessor = new MonsterEngagementTickProcessor(strongRegistry, world.Collision, world.PathProvider, TimeProvider.System);
        var strongMob = strongRegistry.AllInstances[0];
        strongMob.TryAcquireTarget(AccountId, allowChangeTargetWhileChasing: false);

        await strongProcessor.ProcessAsync([target.Session, bystander.Session], CancellationToken.None);

        var damagePacket = await ReadExact(target.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(damagePacket.AsSpan(6)));

        // The bystander must receive nothing at all - prove the wire stays quiet with a bounded ping.
        await bystander.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var bystanderReply = await ReadExact(bystander.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(bystanderReply));
        Assert.Equal(40u, bystander.Session.GameplayState!.State.CurrentHp);

        target.Client.Close();
        bystander.Client.Close();
        await target.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
        await bystander.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Item 9: multiple mobs attacking the SAME player in one processor pass must serialize through
    // that player's own CharacterGameplayStateSession.MutateAsync gate - both hits land, HP
    // reflects both, never a lost update from concurrent mutation.
    [Fact]
    public async Task ProcessAsync_TwoMobsAttackingTheSamePlayer_BothHitsApplySerializedHpMutation()
    {
        var allocator = new WorldActorIdAllocator();
        var mobDefinition = GeneratedMobs.GPoring with { Attack = 1000 };
        var spawnA = new MobSpawnDefinition(mobDefinition, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var spawnB = new MobSpawnDefinition(mobDefinition, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 1));
        var registry = new MonsterRegistry([spawnA, spawnB], allocator, new FixedCellSelector(76, 51), TimeProvider.System);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules());
        var processor = new MonsterEngagementTickProcessor(registry, EmptyMapCollisionProvider.Instance, new UnverifiedGridLineMovementPathProvider(), TimeProvider.System);
        var mobA = registry.AllInstances[0];
        var mobB = registry.AllInstances[1];

        // A large starting HP pool (not this class' usual 40) so BOTH hits land against a live
        // target and are individually observable - the point of this test is proving serialized
        // application of two hits within one tick, not proving lethal/overkill behavior (which
        // ApplyIncomingMobBasicAttackAsync's own "already dead" no-op branch already covers
        // elsewhere).
        var player = await ConnectSessionAsync(registry, combat, AccountId, CharId, 75, 51, "int_land03", FreshNovice(hp: 100_000), visibleMonsterCount: 2);
        using var _dispose = player.Client;

        mobA.TryAcquireTarget(AccountId, allowChangeTargetWhileChasing: false);
        mobB.TryAcquireTarget(AccountId, allowChangeTargetWhileChasing: false);

        await processor.ProcessAsync([player.Session], CancellationToken.None);

        var firstDamagePacket = await ReadExact(player.Stream, PacketConstants.ZcNotifyAct3Length);
        var firstHpPacket = await ReadExact(player.Stream, 8);
        var secondDamagePacket = await ReadExact(player.Stream, PacketConstants.ZcNotifyAct3Length);
        var secondHpPacket = await ReadExact(player.Stream, 8);

        var firstDamage = BinaryPrimitives.ReadUInt32LittleEndian(firstDamagePacket.AsSpan(22));
        var secondDamage = BinaryPrimitives.ReadUInt32LittleEndian(secondDamagePacket.AsSpan(22));
        var hpAfterFirst = BinaryPrimitives.ReadUInt32LittleEndian(firstHpPacket.AsSpan(4));
        var hpAfterSecond = BinaryPrimitives.ReadUInt32LittleEndian(secondHpPacket.AsSpan(4));

        Assert.Equal(100_000u - firstDamage, hpAfterFirst);
        Assert.Equal(hpAfterFirst - secondDamage, hpAfterSecond);
        Assert.Equal(hpAfterSecond, player.Session.GameplayState!.State.CurrentHp);

        player.Client.Close();
        await player.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
