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

// Step 6 final correctness pass, item 1: deterministic tests for the race between an attacker's own
// in-flight confirmed-lethal-hit projection (PerformDueRepeatAttackAsync's own TryMarkMonsterDeadAsync
// call) and World's independent, authoritative Died feed reaching the SAME session through
// MapTcpServer's separate monster-tick loop (FanOutEntryAsync -> NotifyMonsterDiedAsync) for the SAME
// exact life - see LethalDeathProjectionArbiter's own doc comment for the full race this closes. Each
// interleaving is forced DETERMINISTICALLY via FakeCombatWorldRuntime.BeforeTryMarkMonsterDeadReturns,
// which runs synchronously at the exact moment the real Orleans RPC would still be "in flight" -
// simulating the SEPARATE feed-loop's own NotifyMonsterDiedAsync call landing at precisely that point,
// with no reliance on real thread timing/genuine concurrency.
public sealed class MapClientSessionLethalDeathProjectionRaceTests
{
    private const uint AccountId = 61;
    private const uint CharId = 63;

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

    private sealed record Scenario(
        TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask,
        MonsterCombatStateStore CombatState, FakeCombatWorldRuntime FakeWorld, MonsterRegistry Registry,
        string MapId, WorldSimulationEpoch Epoch, uint ActorId, WorldMonsterIncarnationId Incarnation, uint MaxHp);

    private static async Task<Scenario> SetupAsync(FakeCombatWorldRuntime fakeWorld, uint maxHp = 1)
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

        var gameplayPersistence = new RecordingGameplayStatePersistence(StrongAttacker());
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsterProjections: monsterProjections, combat: combat,
            combatState: combatState, distributedWorld: fakeWorld);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15);
        await ReadExact(stream, 6);
        await ReadExact(stream, 4);
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));

        return new Scenario(client, stream, session, run, combatState, fakeWorld, registry, target.Map, epoch, actorId, incarnation, maxHp);
    }

    // Interleaving 1: lethal attempt starts -> World returns MarkedDead -> the Died feed reaches the
    // attacker's OWN session BEFORE the local lethal projection continues past TryMarkMonsterDeadAsync
    // -> the attacker must still ultimately receive exactly one 0x08C8, one 0x0977 hp=0, one 0x0080
    // reason=died, in that order, with no duplicate vanish.
    [Fact]
    public async Task Interleaving1_DiedFeedArrivesBeforeLocalProjectionContinues_AttackerReceivesExactlyOneOfEachPacket_NoDuplicateVanish()
    {
        var fakeWorld = new FakeCombatWorldRuntime();
        var scenario = await SetupAsync(fakeWorld);
        using var disposableClient = scenario.Client;

        var life = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        fakeWorld.BeforeTryMarkMonsterDeadReturns = async () =>
        {
            // Simulate the SEPARATE MapTcpServer monster-tick loop observing World's own Died feed
            // for this EXACT life while TryMarkMonsterDeadAsync is still "in flight" (BeginInFlight
            // was already called by PerformDueRepeatAttackAsync before this RPC call started).
            await scenario.Session.NotifyMonsterDiedAsync(life, CancellationToken.None);
        };

        await scenario.Stream.WriteAsync(AttackPacket(scenario.ActorId));

        // Live-acceptance wire-fidelity fix: pinned unit_attack's own due-now branch (unit.cpp:
        // 2971-2978) sends clif_fixpos (0x0088, the ATTACKER's own current position)
        // unconditionally, before the attack-timer-equivalent execution/World lethal-RPC race
        // exercised below.
        var fixposPacket = await ReadExact(scenario.Stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(fixposPacket.AsSpan(2)));

        // Read the damage packet (0x08C8, fixed length), HP-info (0x0977 hp=0, fixed length), and
        // vanish (0x0080 died, fixed length) - in that exact order - and confirm nothing else (no
        // duplicate vanish) follows.
        var damagePacket = await ReadExact(scenario.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));

        var hpInfoPacket = await ReadExact(scenario.Stream, PacketConstants.ZcHpInfoLength);
        Assert.Equal((short)PacketConstants.ZcHpInfo, BinaryPrimitives.ReadInt16LittleEndian(hpInfoPacket));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(6))); // hp=0.

        var vanishPacket = await ReadExact(scenario.Stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(scenario.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(vanishPacket.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanishPacket[6]);

        // No duplicate vanish/second death sequence must follow - confirmed by a harmless ping
        // round-trip landing next.
        await scenario.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(scenario.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        Assert.True(fakeWorld.IsConfirmedDead(life));
        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 6's own final race closure (previously-uncovered interleaving, distinct from
    // Interleaving1 above): TryMarkMonsterDeadAsync has ALREADY returned MarkedDead to the caller -
    // the RPC itself is no longer "in flight" from PerformDueRepeatAttackAsync's own perspective -
    // but CommitConfirmedDeath has not yet run. World's Died feed reaching NotifyMonsterDiedAsync
    // for this exact life in EXACTLY that window must still be deferred (the arbiter registration is
    // deliberately kept open through this entire span, not completed immediately after MarkedDead -
    // see the CommitConfirmedDeath call site's own doc comment). The attacker must still ultimately
    // receive exactly one 0x08C8, one 0x0977 hp=0, one 0x0080 reason=died, with no duplicate vanish -
    // proving the fix actually closes the SMALLER post-RPC race window the earlier CompleteInFlight-
    // right-after-MarkedDead shape left open.
    [Fact]
    public async Task PostMarkedDead_BeforeCommitConfirmedDeath_DiedFeedArrives_IsStillDeferred_AttackerReceivesExactlyOneOfEachPacket_NoDuplicateVanish()
    {
        var fakeWorld = new FakeCombatWorldRuntime();
        var scenario = await SetupAsync(fakeWorld);
        using var disposableClient = scenario.Client;

        var life = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        // Deliberately NOT set on FakeCombatWorldRuntime (BeforeTryMarkMonsterDeadReturns fires while
        // the RPC call is still executing) - this hook fires on MapClientSession itself, strictly
        // AFTER TryMarkMonsterDeadAsync has already returned MarkedDead and the caller has already
        // passed its own status check, immediately before CommitConfirmedDeath is called.
        scenario.Session.DebugBeforeCommitConfirmedDeathAsync = async () =>
        {
            await scenario.Session.NotifyMonsterDiedAsync(life, CancellationToken.None);
        };

        await scenario.Stream.WriteAsync(AttackPacket(scenario.ActorId));

        // Live-acceptance wire-fidelity fix: the due-now fixpos precedes this due-now hit
        // regardless of the later post-MarkedDead race exercised below.
        var fixposPacket = await ReadExact(scenario.Stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(fixposPacket.AsSpan(2)));

        var damagePacket = await ReadExact(scenario.Stream, PacketConstants.ZcNotifyAct3Length);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(damagePacket));

        var hpInfoPacket = await ReadExact(scenario.Stream, PacketConstants.ZcHpInfoLength);
        Assert.Equal((short)PacketConstants.ZcHpInfo, BinaryPrimitives.ReadInt16LittleEndian(hpInfoPacket));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(hpInfoPacket.AsSpan(6)));

        var vanishPacket = await ReadExact(scenario.Stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(scenario.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(vanishPacket.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanishPacket[6]);

        // No duplicate vanish - confirmed by a harmless ping round-trip landing next.
        await scenario.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(scenario.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        Assert.True(fakeWorld.IsConfirmedDead(life));
        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Item 1's own requirement F: MarkedDead was confirmed by World, but this session's own local
    // CommitConfirmedDeath unexpectedly returns non-Applied (an unexpected local-state mismatch) - a
    // Died that was deferred while pending must NOT be silently discarded; the deferred authoritative
    // vanish cleanup must still be performed exactly once even though this session never reaches its
    // own attacker-owned wire sequence.
    [Fact]
    public async Task MarkedDead_DiedDeferred_CommitConfirmedDeathUnexpectedlyReturnsNonApplied_DeferredDiedCleanupIsNotLost()
    {
        var fakeWorld = new FakeCombatWorldRuntime();
        // maxHp: 1 registered normally by SetupAsync, but this test forces CommitConfirmedDeath's own
        // non-Applied path by removing the local combat-state key entirely right before it would run -
        // simulating "the local life is no longer registered" (an unexpected local-state mismatch)
        // despite World having already confirmed MarkedDead moments earlier.
        var scenario = await SetupAsync(fakeWorld);
        using var disposableClient = scenario.Client;

        var life = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        scenario.Session.DebugBeforeCommitConfirmedDeathAsync = async () =>
        {
            await scenario.Session.NotifyMonsterDiedAsync(life, CancellationToken.None);
            // Force CommitConfirmedDeath's own non-Applied path: remove the local combat-state entry
            // out from under it immediately after the Died feed was deferred, simulating an
            // unexpected local-state mismatch at the exact moment World already confirmed MarkedDead.
            scenario.CombatState.Remove(MonsterCombatKey.From(life));
        };

        await scenario.Stream.WriteAsync(AttackPacket(scenario.ActorId));

        // Live-acceptance wire-fidelity fix: the due-now fixpos precedes this due-now hit
        // regardless of the later CommitConfirmedDead non-Applied race exercised below.
        var fixposPacket = await ReadExact(scenario.Stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(fixposPacket.AsSpan(2)));

        // The deferred authoritative vanish must still arrive - reason=Died, exactly once - even
        // though this session's own CommitConfirmedDeath never actually confirmed a local kill.
        var vanishPacket = await ReadExact(scenario.Stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(scenario.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(vanishPacket.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanishPacket[6]);

        // No damage/HP-info/reward packet - this session never owned a successful local projection.
        // No duplicate vanish either - confirmed by a harmless ping landing next.
        await scenario.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(scenario.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        Assert.True(fakeWorld.IsConfirmedDead(life));
        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Interleaving 2: lethal RPC is in flight -> Died feed arrives -> RPC then fails transiently ->
    // no local reward/damage ownership -> the authoritative Died vanish is eventually sent exactly
    // once -> the repeat target does not continue attacking the dead life.
    [Fact]
    public async Task Interleaving2_DiedFeedArrivesThenRpcFailsTransiently_NoRewardOwnership_DeferredVanishSentExactlyOnce_RepeatTargetStops()
    {
        var fakeWorld = new FakeCombatWorldRuntime { ThrowTransientTryMarkMonsterDeadCount = 1 };
        var scenario = await SetupAsync(fakeWorld);
        using var disposableClient = scenario.Client;

        var life = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        var notifyCallCount = 0;
        fakeWorld.BeforeTryMarkMonsterDeadReturns = async () =>
        {
            notifyCallCount++;
            if (notifyCallCount == 1) // Only on the FIRST (the one that will throw) attempt.
                await scenario.Session.NotifyMonsterDiedAsync(life, CancellationToken.None);
        };

        await scenario.Stream.WriteAsync(AttackPacket(scenario.ActorId));

        // Live-acceptance wire-fidelity fix: the due-now fixpos precedes this due-now hit
        // regardless of the transient RPC failure exercised below.
        var fixposPacket = await ReadExact(scenario.Stream, PacketConstants.ZcStopMoveLength);
        Assert.Equal((short)PacketConstants.ZcStopMove, BinaryPrimitives.ReadInt16LittleEndian(fixposPacket));
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(fixposPacket.AsSpan(2)));

        // The deferred authoritative vanish must arrive - reason=Died, exactly once - even though
        // the RPC that would have let THIS session claim ownership of the kill failed transiently.
        var vanishPacket = await ReadExact(scenario.Stream, PacketConstants.ZcNotifyVanishLength);
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(vanishPacket));
        Assert.Equal(scenario.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(vanishPacket.AsSpan(2)));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, vanishPacket[6]);

        // No damage/HP-info/reward packet must EVER arrive - this session never gets to claim the
        // kill. No duplicate vanish either. Confirmed by a harmless ping landing next.
        await scenario.Stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = await ReadExact(scenario.Stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        // Local combat state must remain untouched by this session's own attempt (the transient
        // failure happened before ANY local mutation - the store itself is a completely separate
        // question from whether World's OWN Died feed later reaps the entry via the real production
        // MonsterFeedProjection/MapTcpServer path, which this focused unit test does not drive).
        var key = new MonsterCombatKey(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        Assert.True(scenario.CombatState.TryGet(key, out var state));
        Assert.Equal(scenario.MaxHp, state.CurrentHp);

        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Interleaving 3: a Died feed entry for a DIFFERENT incarnation of the SAME ActorId must NOT be
    // suppressed by an in-flight lethal projection registered for another (already-superseded)
    // incarnation - proven directly against LethalDeathProjectionArbiter, the exact type this
    // guarantee lives in, since driving this specific scenario through the full wire path would
    // require an actual incarnation change mid-attack (a separate, already-covered scenario
    // elsewhere) rather than adding anything new to prove about THIS type's own key-exactness.
    [Fact]
    public void Interleaving3_DiedForDifferentIncarnationOfSameActorId_IsNotSuppressedByInFlightProjectionForAnotherIncarnation()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var oldIncarnation = WorldMonsterIncarnationId.First;
        var newIncarnation = oldIncarnation.Next();
        var lifeOld = new WorldMonsterLifeReference("int_land03", WorldSimulationEpoch.NewEpoch(), 1, oldIncarnation);
        var epoch = lifeOld.SimulationEpoch;
        var lifeNew = new WorldMonsterLifeReference("int_land03", epoch, 1, newIncarnation);

        arbiter.BeginInFlight(lifeOld);

        // A Died feed entry for the NEW incarnation (a different life entirely, despite sharing the
        // same ActorId) must NOT be deferred - there is no in-flight projection registered for it.
        var deferred = arbiter.TryDeferDiedWhileInFlight(lifeNew);

        Assert.False(deferred, "A Died feed entry for a DIFFERENT incarnation must never be suppressed by an in-flight projection registered for another incarnation.");
        // The OLD incarnation's own in-flight registration is untouched by that unrelated check.
        Assert.True(arbiter.TryDeferDiedWhileInFlight(lifeOld));
    }

    // Interleaving 4: an ordinary bystander session (no in-flight local lethal projection for this
    // life at all) must still receive Died immediately, unchanged - proven directly against
    // LethalDeathProjectionArbiter for the exact same reason as interleaving 3 above.
    [Fact]
    public void Interleaving4_BystanderSession_DiedIsDeliveredImmediately_Unchanged()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var life = new WorldMonsterLifeReference("int_land03", WorldSimulationEpoch.NewEpoch(), 1, WorldMonsterIncarnationId.First);

        // No BeginInFlight call was ever made for this session/life - an ordinary bystander.
        var deferred = arbiter.TryDeferDiedWhileInFlight(life);

        Assert.False(deferred, "A bystander session with no in-flight local lethal projection must never have its Died delivery suppressed.");
    }
}
