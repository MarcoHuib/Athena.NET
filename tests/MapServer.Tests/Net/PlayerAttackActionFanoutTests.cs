using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Live multiplayer regression: a player's attack on a monster is an AREA-visible combat ACTION in
// pinned rAthena (clif_damage -> clif_send(..., AREA) - see PlayerAttackActionOutcome's own doc
// comment), exactly like a monster's own attack already is (MonsterAttackActionOutcome). Before this
// fix, MapClientSession wrote the 0x08C8 action directly to its own socket only, so a nearby second
// player never saw the attacking player's animation even though both shared the same authoritative
// World monster state. These tests drive TWO real MapClientSession instances through the SAME
// MapTcpServer (the local fan-out composition boundary - MapTcpServer.FanOutPlayerAttackActionAsync)
// against a SHARED FakeCombatWorldRuntime: World's damage authority is exercised exactly once per hit
// (only the RPC transport is faked, matching every other combat test in this Net/ folder); MapServer's
// own local packet projection is what is actually under test here.
//
// ARCHITECTURE NOTE: this fan-out (and these tests) cover only sessions on the SAME MapServer gateway
// process - see MapTcpServer.FanOutPlayerAttackActionAsync's own doc comment for the still-open
// cross-replica gap.
public sealed class PlayerAttackActionFanoutTests
{
    private const string MapId = "int_land";
    private const uint AttackerAccountId = 2_000_050;
    private const uint AttackerCharId = 10_002;
    private const uint ObserverAccountId = 2_000_000;
    private const uint ObserverCharId = 1;
    private const uint OutOfRangeAccountId = 2_000_100;
    private const uint OtherMapAccountId = 2_000_200;

    private static MapConfigStore ConfigStore() => new(new MapConfig(), "unused.conf");

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
    {
        public bool TrySelectCell(MobSpawnDefinition spawn, int instanceIndex, out MobPosition position) { position = new MobPosition(x, y); return true; }
    }

    private sealed record Player(MapClientSession Session, TcpClient Client, NetworkStream Stream, Task Run);

    // Deliberately weak (fresh-Novice 9/9/9/9/9/9) so a single hit never one-shots Poring's 55 HP -
    // these tests need a stable, observable non-lethal action.
    private static CharacterGameplayState WeakAttacker(uint charId) => new(
        CharacterId: charId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1, BaseExperience: 0, JobExperience: 0,
        CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10, StatPoints: 0, SkillPoints: 0,
        Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    private sealed record Fixture(MapTcpServer Server, MonsterFeedProjectionRegistry Projections, MonsterCombatCoordinator Combat, MonsterAttackCadenceStore CombatState,
        FakeCombatWorldRuntime FakeWorld, MobInstance Target, WorldSimulationEpoch Epoch, PlayerPresenceRegistry Players, PlayerVisibilityCoordinator PlayerVisibility);

    private static Fixture MakeFixture()
    {
        var allocator = new WorldActorIdAllocator();
        var spawnDefinition = new MobSpawnDefinition(GeneratedMobs.GPoring, MapId, 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([spawnDefinition], allocator.Allocate, new FixedCellSelector(100, 100), TimeProvider.System);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        var combatState = new MonsterAttackCadenceStore();
        combatState.Register(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value));
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules());
        var fakeWorld = new FakeCombatWorldRuntime();
        var projections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances, fakeWorld);
        var world = new MapServerWorld(WorldMapRegistry.Tutorial, [], combat, EmptyMapCollisionProvider.Instance, new UnverifiedGridLineMovementPathProvider(), projections, combatState);
        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, fakeWorld);
        return new Fixture(server, projections, combat, combatState, fakeWorld, target, epoch, world.Players, world.PlayerVisibility);
    }

    private static void InjectSession(MapTcpServer server, int sessionId, MapClientSession session)
    {
        var field = typeof(MapTcpServer).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MapTcpServer._sessions field not found - test seam broken by a rename.");
        var sessions = (ConcurrentDictionary<int, MapClientSession>)field.GetValue(server)!;
        sessions[sessionId] = session;
    }

    private static async Task<Player> ConnectAsync(Fixture fixture, int sessionId, uint accountId, uint charId, string mapId, ushort x, ushort y, bool wireFanout = true)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var session = new MapClientSession(
            sessionId, serverClient, new CharServerConnector(ConfigStore()), iroAuthenticated: true, mapId, x, y,
            gameplayStatePersistence: new FixedGameplayStatePersistence(WeakAttacker(charId)),
            accountId: accountId, charId: charId,
            monsterProjections: fixture.Projections, combat: fixture.Combat, combatState: fixture.CombatState,
            players: fixture.Players, playerVisibility: fixture.PlayerVisibility,
            distributedWorld: fixture.FakeWorld,
            playerAttackFanout: wireFanout ? fixture.Server.FanOutPlayerAttackActionAsync : null,
            lethalAttackGate: fixture.Server.LethalAttackGateForTest);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(accountId, charId, 1, 2, 0, 0, false, mapId, x, y, 0, 0, 0, CharacterName: $"P{accountId}"));
        await ReadExact(stream, 29);
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon
        await ReadExact(stream, 6);  // inventory start
        await ReadExact(stream, 4);  // inventory end
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(session.IsWorldMapEligible);
        InjectSession(fixture.Server, sessionId, session);
        return new Player(session, client, stream, run);
    }

    // Item 14 §4: local AOI enter-fanout is now feed-driven ONLY - a session's own World
    // registration (already completed inside ConnectAsync above) becomes locally client-visible
    // only once MapTcpServer's own per-map tick loop polls the player feed and reconciles
    // PlayerVisibilityCoordinator (see PlayerFeedProjection's own doc comment). This fixture never
    // runs a real background tick loop (server.RunAsync is never called - only each session's own
    // RunAsync), so tests must explicitly drive one production tick via
    // ProcessOneMonsterTickAsync (the exact same method the real 100ms loop calls) after every
    // connection and after every attack whose cross-observer visibility the test asserts on.
    private static async Task DrivePlayerTickAsync(Fixture fixture)
    {
        var sessionsField = typeof(MapTcpServer).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MapTcpServer._sessions field not found - test seam broken by a rename.");
        var sessions = (ConcurrentDictionary<int, MapClientSession>)sessionsField.GetValue(fixture.Server)!;
        await fixture.Server.ProcessOnePlayerTickAsync(sessions.Values.ToArray(), CancellationToken.None);
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

    private static bool IsMonsterIntro(byte[] packet, uint actorId) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyStandEntry && packet[4] == 5 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == actorId;

    private static bool IsPlayerIntro(byte[] packet, uint actorId) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) is 0x09fe or 0x09ff && packet[4] == 0 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == actorId;

    private static byte[] AttackPacket(uint targetActorId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzAttackRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), targetActorId);
        packet[6] = 7; // DMG_REPEAT
        packet[7] = 0x7f;
        return packet;
    }

    private static async Task ReadFixposAsync(Stream stream, uint accountId)
    {
        var fixpos = await ReadExact(stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixpos));
        Assert.Equal(accountId, BinaryPrimitives.ReadUInt32LittleEndian(fixpos.AsSpan(2)));
    }

    private static void AssertAction(byte[] packet, uint attackerActorId, uint mobActorId)
    {
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(attackerActorId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal(mobActorId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6)));
    }

    private static async Task AssertNothingMoreSentAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));
    }

    // Item 14: production now delivers every OTHER observer's copy of a player's attack action via
    // the World feed (MapTcpServer.FanOutEntryAsync), not via FanOutPlayerAttackActionAsync's
    // same-process broadcast (that method is now only the ATTACKER's own immediate fast-path echo -
    // see its own doc comment). FakeCombatWorldRuntime deliberately never implements a real
    // PollMonsterFeedAsync (see this file's own top-of-file doc comment and WorldMonsterProjectionTestHelper's),
    // so these tests inject the equivalent feed entry directly into FanOutEntryAsync after the
    // attack's own RPC has resolved - the same technique this file already uses for Died entries
    // (see LethalHit_RacingDiedFeedEvent's own doc comment) - reproducing exactly what the real
    // per-map tick loop would have delivered from a genuine PollMonsterFeedAsync page.
    private static async Task FanOutSynthesizedPlayerAttackActionAsync(Fixture fixture, MapClientSession attackerSession, uint attackerActorId, CancellationToken cancellationToken = default)
    {
        var command = fixture.FakeWorld.LastApplyMonsterDamageCommand ?? throw new InvalidOperationException("Expected ApplyMonsterDamageAsync to have already been called.");
        var hpAfter = fixture.FakeWorld.TryGetCurrentHp(command.Life) ?? throw new InvalidOperationException("Expected the fake World ledger to have HP for this life.");
        var playerAttack = new WorldPlayerAttackAction(attackerActorId, command.AttackerCharacterId, command.AttackerPresenceId, command.Damage, 0, 0, Hit: command.Damage > 0, Lethal: hpAfter == 0);
        var instance = fixture.Target.ToWorldMonsterInstance() with { CurrentHp = hpAfter };
        // Sequence is -1 to match ProjectPlayerAttackActionAsync's own fallback for a fake World
        // whose WorldMonsterDamageResult.AttackActionSequence is always null (FakeCombatWorldRuntime
        // never populates it) - the fast-path echo's dedup key uses that SAME fallback value, so this
        // synthesized entry must match it exactly for WasPlayerAttackActionLocallyEchoed to dedup
        // correctly (see MapClientSession.ProjectPlayerAttackActionAsync's own doc comment).
        var entry = new WorldMonsterFeedEntry(-1, WorldMonsterFeedEntryKind.PlayerAttackAction, fixture.Target.ActorId, instance.IncarnationId, instance, PlayerAttack: playerAttack);
        var sessionsField = typeof(MapTcpServer).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MapTcpServer._sessions field not found - test seam broken by a rename.");
        var sessions = (ConcurrentDictionary<int, MapClientSession>)sessionsField.GetValue(fixture.Server)!;
        // Excludes the attacker's OWN session: in real production the dedup mark
        // (MarkPlayerAttackActionLocallyEchoed) is set synchronously the instant
        // ProjectPlayerAttackActionAsync is entered, well before World's RPC result could ever reach
        // a concurrently-running feed poll (the RPC has to complete first, and the feed can only
        // observe an entry AFTER that same commit) - so a real feed dispatch reaching the attacker's
        // own session always finds the mark already set and skips it. This synthesized/out-of-band
        // injection has no such ordering guarantee against this SPECIFIC test's own
        // DebugBeforeLethalActionFanoutAsync barrier (a test-only seam that pauses the attacker
        // BEFORE its own mark is set, specifically to test Died gating) - excluding the attacker
        // here reproduces the real dedup OUTCOME without depending on timing this test deliberately
        // does not control.
        await fixture.Server.FanOutEntryAsync(entry, fixture.Epoch, sessions.Values.Where(session => session.AccountId != attackerSession.AccountId).ToArray(), cancellationToken);
    }

    // Connects the attacker first (so it discovers the monster alone), then the observer (which
    // discovers both the pre-existing attacker and the monster); drains every discovery packet so
    // both streams start clean for the attack itself.
    private static async Task<(Player Attacker, Player Observer, uint MonsterActorId)> ConnectAttackerAndObserverAsync(Fixture fixture)
    {
        var attacker = await ConnectAsync(fixture, 1, AttackerAccountId, AttackerCharId, MapId, 99, 100);
        var monsterFromAttacker = await ReadDynamic(attacker.Stream);
        Assert.True(IsMonsterIntro(monsterFromAttacker, fixture.Target.ActorId));

        var observer = await ConnectAsync(fixture, 2, ObserverAccountId, ObserverCharId, MapId, 101, 100);
        // Item 14 §4: player-to-player AOI enter-fanout is feed-driven - drive one production tick
        // (the same ProcessOneMonsterTickAsync the real 100ms loop calls) so each session's World
        // registration is reconciled into PlayerVisibilityCoordinator and the mutual 0x09FE/0x09FF
        // discovery packets are actually sent.
        await DrivePlayerTickAsync(fixture);
        await ReadDynamic(attacker.Stream); // Attacker sees the newly-entered observer (0x09FE) - drained, not the focus of this file.
        var observerBurst = new[] { await ReadDynamic(observer.Stream), await ReadDynamic(observer.Stream) };
        Assert.Contains(observerBurst, p => IsMonsterIntro(p, fixture.Target.ActorId));
        Assert.Contains(observerBurst, p => IsPlayerIntro(p, AttackerAccountId));

        return (attacker, observer, fixture.Target.ActorId);
    }

    [Fact]
    public async Task NonLethalHit_ProjectsTheSameActionToBothTheAttackerAndANearbyObserver()
    {
        var fixture = MakeFixture();
        var (attacker, observer, monsterActorId) = await ConnectAttackerAndObserverAsync(fixture);
        using var _a = attacker.Client;
        using var _o = observer.Client;

        await attacker.Stream.WriteAsync(AttackPacket(monsterActorId));
        await ReadFixposAsync(attacker.Stream, AttackerAccountId);

        var attackerAction = await ReadExact(attacker.Stream, PacketConstants.ZcNotifyAct3Length);
        AssertAction(attackerAction, AttackerAccountId, monsterActorId);
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(attackerAction.AsSpan(22));
        Assert.True(damage > 0, "Expected the test attacker to deal nonzero damage.");
        var attackerHpInfo = await ReadExact(attacker.Stream, PacketConstants.ZcHpInfoLength); // Self-only, unchanged by this fix.
        Assert.Equal((short)PacketConstants.ZcHpInfo, BinaryPrimitives.ReadInt16LittleEndian(attackerHpInfo));

        // Item 14: the observer's copy now arrives via the World feed - see
        // FanOutSynthesizedPlayerAttackActionAsync's own doc comment for why this test injects it
        // directly rather than driving a real PollMonsterFeedAsync loop.
        await FanOutSynthesizedPlayerAttackActionAsync(fixture, attacker.Session, AttackerAccountId);

        // The live regression's fix: the observer receives the SAME action - same source, same
        // target, same authoritative damage - and nothing else (no HP info, no progression).
        var observerAction = await ReadExact(observer.Stream, PacketConstants.ZcNotifyAct3Length);
        AssertAction(observerAction, AttackerAccountId, monsterActorId);
        Assert.Equal(damage, BinaryPrimitives.ReadUInt32LittleEndian(observerAction.AsSpan(22)));
        await AssertNothingMoreSentAsync(observer.Stream);

        // World's damage authority was invoked exactly once for this hit, never once per observer.
        Assert.Equal(1, fixture.FakeWorld.ApplyMonsterDamageCallCount);
    }

    [Fact]
    public async Task NonLethalHit_TheAttackerNeverReceivesADuplicateAction()
    {
        var fixture = MakeFixture();
        var (attacker, observer, monsterActorId) = await ConnectAttackerAndObserverAsync(fixture);
        using var _a = attacker.Client;
        using var _o = observer.Client;

        await attacker.Stream.WriteAsync(AttackPacket(monsterActorId));
        await ReadFixposAsync(attacker.Stream, AttackerAccountId);
        await ReadExact(attacker.Stream, PacketConstants.ZcNotifyAct3Length); // The one action.
        await ReadExact(attacker.Stream, PacketConstants.ZcHpInfoLength);     // The one self-HP-info.

        // Item 14: the later feed-driven dispatch for this SAME action must be deduplicated against
        // the attacker's own already-delivered fast-path echo (WasPlayerAttackActionLocallyEchoed) -
        // this is exactly the dedup this test asserts. Drain the observer's own copy so the fan-out
        // itself completes cleanly (not the focus of this test - see the Nonlethal test above).
        await FanOutSynthesizedPlayerAttackActionAsync(fixture, attacker.Session, AttackerAccountId);
        await ReadExact(observer.Stream, PacketConstants.ZcNotifyAct3Length);

        // Nothing more queued for the attacker's own stream - specifically NOT a second 0x08C8 from
        // the fan-out loop re-notifying the attacker's own session a second time.
        await AssertNothingMoreSentAsync(attacker.Stream);
    }

    [Fact]
    public async Task NonLethalHit_DoesNotReachAnObserverOutsideTheMonstersAreaOfInterest()
    {
        var fixture = MakeFixture();
        var (attacker, observer, monsterActorId) = await ConnectAttackerAndObserverAsync(fixture);
        using var _a = attacker.Client;
        using var _o = observer.Client;

        // Far enough from the monster (100,100) that this session's own map-load discovery burst never
        // marks it visible (WorldVisibilityOptions.DefaultAreaSize is 14) - it never becomes an
        // observer of anything on this map.
        var outOfRange = await ConnectAsync(fixture, 3, OutOfRangeAccountId, 3, MapId, 100, 200);
        using var _oor = outOfRange.Client;
        await AssertNothingMoreSentAsync(outOfRange.Stream); // No monster/player discovery reached it either.

        await attacker.Stream.WriteAsync(AttackPacket(monsterActorId));
        await ReadFixposAsync(attacker.Stream, AttackerAccountId);
        await ReadExact(attacker.Stream, PacketConstants.ZcNotifyAct3Length);
        await ReadExact(attacker.Stream, PacketConstants.ZcHpInfoLength);
        await FanOutSynthesizedPlayerAttackActionAsync(fixture, attacker.Session, AttackerAccountId);
        await ReadExact(observer.Stream, PacketConstants.ZcNotifyAct3Length); // The in-range observer still gets it.

        await AssertNothingMoreSentAsync(outOfRange.Stream);
    }

    [Fact]
    public async Task NonLethalHit_DoesNotReachASessionOnADifferentMap()
    {
        var fixture = MakeFixture();
        var (attacker, observer, monsterActorId) = await ConnectAttackerAndObserverAsync(fixture);
        using var _a = attacker.Client;
        using var _o = observer.Client;

        var otherMap = await ConnectAsync(fixture, 4, OtherMapAccountId, 4, "izlude", 100, 100);
        using var _om = otherMap.Client;
        await AssertNothingMoreSentAsync(otherMap.Stream);

        await attacker.Stream.WriteAsync(AttackPacket(monsterActorId));
        await ReadFixposAsync(attacker.Stream, AttackerAccountId);
        await ReadExact(attacker.Stream, PacketConstants.ZcNotifyAct3Length);
        await ReadExact(attacker.Stream, PacketConstants.ZcHpInfoLength);
        await FanOutSynthesizedPlayerAttackActionAsync(fixture, attacker.Session, AttackerAccountId);
        await ReadExact(observer.Stream, PacketConstants.ZcNotifyAct3Length);

        await AssertNothingMoreSentAsync(otherMap.Stream);
    }

    // Live multiplayer regression: World's Died feed for a lethal hit can become independently
    // observable (via MapTcpServer's own monster-tick loop) concurrently with the attacking session
    // still building/fanning out its own final action - without ordering, an observer whose own Died
    // dispatch wins that race would see the monster vanish with NO preceding action (or lose the
    // action entirely, since NotifyPlayerAttackActionAsync's own - still correct - visibility check
    // then finds the monster already marked not-visible). This test forces exactly that race
    // deterministically, using a real barrier (DebugBeforeLethalActionFanoutAsync + a directly
    // injected Died feed entry via MapTcpServer.FanOutEntryAsync, since FakeCombatWorldRuntime
    // deliberately does not implement PollMonsterFeedAsync) - never Task.Delay/timing as the
    // synchronization mechanism itself.
    [Fact]
    public async Task LethalHit_RacingDiedFeedEvent_ObserverStillReceivesTheActionBeforeTheVanish_NoDuplicates()
    {
        var fixture = MakeFixture();
        var life = new WorldMonsterLifeReference(fixture.Target.Map, fixture.Epoch, fixture.Target.ActorId, new WorldMonsterIncarnationId(fixture.Target.IncarnationId.Value));
        fixture.FakeWorld.SeedMonster(life, currentHp: 1, maxHp: fixture.Target.Spawn.Mob.MaxHp); // Guarantees a one-shot kill from any nonzero hit.

        var (attacker, observer, monsterActorId) = await ConnectAttackerAndObserverAsync(fixture);
        using var _a = attacker.Client;
        using var _o = observer.Client;

        var readyToRaceDied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAttackerFanout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        attacker.Session.DebugBeforeLethalActionFanoutAsync = async () =>
        {
            readyToRaceDied.TrySetResult();
            await releaseAttackerFanout.Task;
        };

        await attacker.Stream.WriteAsync(AttackPacket(monsterActorId));
        await ReadFixposAsync(attacker.Stream, AttackerAccountId);

        // Barrier: the attacker's own lethal tail has World's Applied+KilledByThisHit result in hand
        // (LethalAttackProjectionGate is already OPEN for this life - Enter ran before the RPC was even
        // dispatched) and is paused immediately before fanning its action out.
        await readyToRaceDied.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Item 14: production would have already dispatched this SAME poll's PlayerAttackAction
        // entry to the observer before ever reaching the Died entry (same feed, same page, feed
        // order preserved by FanOutEntryAsync's own per-entry loop) - inject it here, before the
        // Died race below, to reproduce that exact ordering. Unlike the pre-item-14 world, this
        // delivery does NOT wait on LethalAttackProjectionGate at all - the gate only orders the
        // ATTACKER's own same-process fast-path echo against Died (see LethalAttackProjectionGate's
        // own doc comment); the feed's delivery to the OBSERVER is structurally already ordered
        // ahead of Died by feed sequence alone, independent of the gate/attacker's own paused tail.
        // The observer therefore receives its action packet HERE, deterministically, before the
        // still-gated Died dispatch below - reading it now (rather than asserting nothing arrived)
        // is what actually proves the item 14 invariant this test exists for.
        await FanOutSynthesizedPlayerAttackActionAsync(fixture, attacker.Session, AttackerAccountId);
        var observerAction = await ReadExact(observer.Stream, PacketConstants.ZcNotifyAct3Length);
        AssertAction(observerAction, AttackerAccountId, monsterActorId);

        // Inject the matching authoritative Died feed event directly - this drives EXACTLY
        // MapTcpServer.FanOutEntryAsync's own Died branch, the real production code under test,
        // concurrently with the still-paused attacker task. It must genuinely block on the still-open
        // gate rather than racing ahead to notify the observer first.
        var diedEntry = new WorldMonsterFeedEntry(Sequence: 1, WorldMonsterFeedEntryKind.Died, monsterActorId,
            new WorldMonsterIncarnationId(fixture.Target.IncarnationId.Value),
            fixture.Target.ToWorldMonsterInstance() with { Lifecycle = WorldMonsterLifecycleState.Dead, CurrentHp = 0 });
        var diedDispatchTask = fixture.Server.FanOutEntryAsync(diedEntry, fixture.Epoch, [attacker.Session, observer.Session], CancellationToken.None);

        // Proof the wait is real, not merely fast: give the scheduler a bounded chance to run the Died
        // dispatch to completion, then assert it genuinely has NOT - it is blocked on the open gate.
        await Task.Delay(50);
        Assert.False(diedDispatchTask.IsCompleted, "Expected FanOutEntryAsync's Died dispatch to still be waiting on the open LethalAttackProjectionGate.");
        await AssertNothingMoreSentAsync(observer.Stream); // The action already arrived above; nothing further (specifically not yet the vanish) has reached the observer.

        releaseAttackerFanout.TrySetResult();
        await diedDispatchTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Observer: the vanish arrives only now, strictly after the action already read above -
        // never the reverse, never missing, never duplicated.
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(observerAction.AsSpan(22)) > 0);
        var observerVanish = await ReadExact(observer.Stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(observerVanish));
        Assert.Equal(monsterActorId, BinaryPrimitives.ReadUInt32LittleEndian(observerVanish.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, observerVanish[6]);
        await AssertNothingMoreSentAsync(observer.Stream); // No duplicate action, no duplicate vanish.

        // Attacker: unaffected - its own action, self-only HP-info(0), then its own single vanish
        // (the injected Died was correctly deferred for the attacker's own in-flight life, exactly
        // like LethalDeathProjectionArbiter already guaranteed before this fix).
        var attackerAction = await ReadExact(attacker.Stream, PacketConstants.ZcNotifyAct3Length);
        AssertAction(attackerAction, AttackerAccountId, monsterActorId);
        var attackerHpInfo = await ReadExact(attacker.Stream, PacketConstants.ZcHpInfoLength);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(attackerHpInfo.AsSpan(6)));
        var attackerVanish = await ReadExact(attacker.Stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(attackerVanish));
        Assert.Equal(monsterActorId, BinaryPrimitives.ReadUInt32LittleEndian(attackerVanish.AsSpan(2)));
        await AssertNothingMoreSentAsync(attacker.Stream); // No duplicate vanish for the attacker either.

        // World's damage authority was invoked exactly once for this hit.
        Assert.Equal(1, fixture.FakeWorld.ApplyMonsterDamageCallCount);
    }

    // A standalone session (no MapTcpServer, no fan-out delegate - every existing single-session
    // combat test fixture in this project) must keep observing its own attack action exactly as
    // before this fix: this proves the fallback branch of ProjectPlayerAttackActionAsync.
    [Fact]
    public async Task NonLethalHit_WithNoFanoutDelegateConfigured_StillNotifiesTheAttackerItself()
    {
        var fixture = MakeFixture();
        var attacker = await ConnectAsync(fixture, 1, AttackerAccountId, AttackerCharId, MapId, 99, 100, wireFanout: false);
        using var _a = attacker.Client;
        await ReadDynamic(attacker.Stream); // Monster discovery.

        await attacker.Stream.WriteAsync(AttackPacket(fixture.Target.ActorId));
        await ReadFixposAsync(attacker.Stream, AttackerAccountId);
        var action = await ReadExact(attacker.Stream, PacketConstants.ZcNotifyAct3Length);
        AssertAction(action, AttackerAccountId, fixture.Target.ActorId);
        await ReadExact(attacker.Stream, PacketConstants.ZcHpInfoLength);
    }
}
