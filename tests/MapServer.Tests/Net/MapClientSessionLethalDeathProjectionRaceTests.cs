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
// in-flight confirmed-lethal-hit projection (PerformDueRepeatAttackAsync's own ApplyMonsterDamageAsync
// call) and World's independent, authoritative Died feed reaching the SAME session through
// MapTcpServer's separate monster-tick loop (FanOutEntryAsync -> NotifyMonsterDiedAsync) for the SAME
// exact life - see LethalDeathProjectionArbiter's own doc comment for the full race this closes.
//
// Substep 9 cutover: TryMarkMonsterDeadAsync/CommitConfirmedDeath are gone entirely - the single
// ApplyMonsterDamageAsync RPC now atomically applies damage AND confirms the lethal transition, so
// there is no longer a separate "MarkedDead already confirmed, but a later local commit step could
// still independently fail" window (the old CommitConfirmedDeath-non-Applied race is structurally
// impossible now - deliberately NOT reproduced here, see this file's own removed-scenario note below).
// Two interleavings are forced DETERMINISTICALLY:
//   - "in flight" (Died arrives while ApplyMonsterDamageAsync itself is still executing) via
//     FakeCombatWorldRuntime.BeforeApplyMonsterDamageReturns, which runs synchronously immediately
//     before the RPC call returns its result (or throws) - simulating the SEPARATE feed loop's own
//     NotifyMonsterDiedAsync call landing at precisely that point.
//   - "post-result, pre-vanish-send" (the RPC has already returned KilledByThisHit=true, but this
//     session has not yet written its own death-vanish packet) via
//     MapClientSession.DebugBeforeMonsterVanishSendAsync, which fires inside HandleLethalDamageResultAsync
//     immediately before the session's own SendMonsterVanishAsync call - the arbiter's in-flight
//     registration is deliberately kept open across this entire span (see HandleLethalDamageResultAsync's
//     own doc comment), so a Died observed in exactly this window must still be deferred correctly.
// Neither relies on real thread timing/genuine concurrency.
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
        MonsterAttackCadenceStore CombatState, FakeCombatWorldRuntime FakeWorld, MonsterRegistry Registry,
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
        var combatState = new MonsterAttackCadenceStore();
        var incarnation = new WorldMonsterIncarnationId(target.IncarnationId.Value);
        combatState.Register(target.Map, epoch, target.ActorId, incarnation);
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules());
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances, fakeWorld);
        // The projection's own snapshot HP (from registry.AllInstances) may not match the scenario's
        // requested maxHp (tests default to a guaranteed-lethal 1 HP) - explicitly (re)seed the fake's
        // own ledger with the scenario's actual maxHp so ApplyMonsterDamageAsync's default HP ledger
        // behaves exactly as each test expects.
        var life = new WorldMonsterLifeReference(target.Map, epoch, target.ActorId, incarnation);
        fakeWorld.SeedMonster(life, maxHp, maxHp);

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

    // Interleaving 1: lethal attempt starts -> Died feed reaches the attacker's OWN session WHILE
    // ApplyMonsterDamageAsync is still "in flight" (before it returns its own lethal result) -> the
    // attacker must still ultimately receive exactly one 0x08C8, one 0x0977 hp=0, one 0x0080
    // reason=died, in that order, with no duplicate vanish.
    [Fact]
    public async Task Interleaving1_DiedFeedArrivesWhileRpcInFlight_AttackerReceivesExactlyOneOfEachPacket_NoDuplicateVanish()
    {
        var fakeWorld = new FakeCombatWorldRuntime();
        var scenario = await SetupAsync(fakeWorld);
        using var disposableClient = scenario.Client;

        var life = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        fakeWorld.BeforeApplyMonsterDamageReturns = async () =>
        {
            // Simulate the SEPARATE MapTcpServer monster-tick loop observing World's own Died feed
            // for this EXACT life while ApplyMonsterDamageAsync is still "in flight" (BeginInFlight
            // was already called by the allocation path before this RPC call started).
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

        Assert.Equal(0u, fakeWorld.TryGetCurrentHp(life));
        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Step 6's own final race closure (previously-uncovered interleaving, distinct from
    // Interleaving1 above): ApplyMonsterDamageAsync has ALREADY returned its own lethal result to the
    // caller - the RPC itself is no longer "in flight" - but this session has not yet written its own
    // death-vanish packet. World's Died feed reaching NotifyMonsterDiedAsync for this exact life in
    // EXACTLY that window must still be deferred (the arbiter registration is deliberately kept open
    // through this entire span, not completed immediately after the RPC returns - see
    // HandleLethalDamageResultAsync's own doc comment). The attacker must still ultimately receive
    // exactly one 0x08C8, one 0x0977 hp=0, one 0x0080 reason=died, with no duplicate vanish - proving
    // the fix actually closes the SMALLER post-RPC race window.
    [Fact]
    public async Task PostRpcResult_BeforeVanishSend_DiedFeedArrives_IsStillDeferred_AttackerReceivesExactlyOneOfEachPacket_NoDuplicateVanish()
    {
        var fakeWorld = new FakeCombatWorldRuntime();
        var scenario = await SetupAsync(fakeWorld);
        using var disposableClient = scenario.Client;

        var life = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        // Deliberately NOT set on FakeCombatWorldRuntime (BeforeApplyMonsterDamageReturns fires while
        // the RPC call is still executing) - this hook fires on MapClientSession itself, strictly
        // AFTER ApplyMonsterDamageAsync has already returned its own lethal result, immediately before
        // the session's own SendMonsterVanishAsync call inside HandleLethalDamageResultAsync.
        scenario.Session.DebugBeforeMonsterVanishSendAsync = async () =>
        {
            await scenario.Session.NotifyMonsterDiedAsync(life, CancellationToken.None);
        };

        await scenario.Stream.WriteAsync(AttackPacket(scenario.ActorId));

        // Live-acceptance wire-fidelity fix: the due-now fixpos precedes this due-now hit
        // regardless of the later post-RPC-result race exercised below.
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

        Assert.Equal(0u, fakeWorld.TryGetCurrentHp(life));
        scenario.Client.Close();
        await scenario.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Interleaving 2: lethal RPC is in flight -> Died feed arrives -> RPC then fails transiently ->
    // no local reward/damage ownership -> the authoritative Died vanish is eventually sent exactly
    // once -> the repeat target does not continue attacking the dead life.
    [Fact]
    public async Task Interleaving2_DiedFeedArrivesThenRpcFailsTransiently_NoRewardOwnership_DeferredVanishSentExactlyOnce_RepeatTargetStops()
    {
        var fakeWorld = new FakeCombatWorldRuntime { ThrowTransientApplyMonsterDamageCount = 1 };
        var scenario = await SetupAsync(fakeWorld);
        using var disposableClient = scenario.Client;

        var life = new WorldMonsterLifeReference(scenario.MapId, scenario.Epoch, scenario.ActorId, scenario.Incarnation);
        var applyCallCount = 0;
        fakeWorld.BeforeApplyMonsterDamageReturns = async () =>
        {
            applyCallCount++;
            if (applyCallCount == 1) // Only on the FIRST (the one that will throw) attempt.
            {
                // Simulate a DIFFERENT attacker's competing hit genuinely winning the kill at World
                // (via the OWN real ledger, not merely delivering the feed entry) - the transient
                // exception below means World never actually processed THIS session's own first
                // attempt, so its LATER retry must observe the life as already dead (AlreadyDead),
                // exactly like a real second ApplyMonsterDamageAsync call against an already-killed
                // life would. Without this ledger mutation, the retry would see HP still intact and
                // legitimately (and correctly, per ApplyMonsterDamageAsync's own semantics) claim the
                // kill itself - which is not the race this test means to exercise.
                fakeWorld.SeedMonster(life, currentHp: 0, maxHp: scenario.MaxHp);
                await scenario.Session.NotifyMonsterDiedAsync(life, CancellationToken.None);
            }
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

        // The fake World's own HP ledger reflects the COMPETING attacker's kill (seeded to 0 above,
        // simulating that attacker's own genuine ApplyMonsterDamageAsync success) - not anything this
        // session's own failed-then-never-retried attempt did. This session's own transient failure
        // happened before ANY local mutation of its own, and it must never get a chance to retry past
        // that point (a dead life must never rearm - see HandleDamageResultAsync's own
        // StaleLifeReference/AlreadyDead handling), so the ledger staying at 0 (rather than reverting
        // to scenario.MaxHp) is exactly the expected end state here.
        Assert.Equal(0u, fakeWorld.TryGetCurrentHp(life));

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
