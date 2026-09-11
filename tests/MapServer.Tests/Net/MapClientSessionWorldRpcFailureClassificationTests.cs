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

// Step 6 hardening (final correctness pass), item 3: exception classification around
// TryMarkMonsterDeadAsync/NotifyMonsterAttackedAsync must be NARROW. A genuinely local
// programming/invariant defect (ArgumentException, an unexpected InvalidOperationException) must
// NEVER be caught and treated as "transient World RPC failure, retry later" - it must propagate and
// fault the session's own repeat-attack loop task, exactly like any other uncaught bug would. This is
// the deliberate opposite of MapClientSessionTransientWorldRpcFailureTests.cs, which proves the
// legitimate transient case (IOException) IS retried.
public sealed class MapClientSessionWorldRpcFailureClassificationTests
{
    private const uint AccountId = 51;
    private const uint CharId = 53;

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

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task LethalHit_TryMarkMonsterDeadThrowsLocalProgrammingDefect_PropagatesAndFaultsTheRepeatAttackLoop_NeverSwallowedAsTransient(Type exceptionType)
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
        var incarnation = new WorldMonsterIncarnationId(target.IncarnationId.Value);
        combatState.Register(target.Map, epoch, target.ActorId, incarnation, maxHp: 1); // 1 HP - guaranteed lethal.
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(), combatState);
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances);

        var gameplayPersistence = new RecordingGameplayStatePersistence(StrongAttacker());
        var fakeWorld = new FakeCombatWorldRuntime
        {
            TryMarkMonsterDeadThrows = () => (Exception)Activator.CreateInstance(exceptionType, "Simulated local programming defect.")!,
            ThrowTransientTryMarkMonsterDeadCount = int.MaxValue, // Every call throws - a "retry later" would hot-loop forever if this were misclassified as transient.
        };

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

        // Step 6 final race closure, item 2: the local programming/invariant defect must NOT be
        // swallowed as "transient, retry later" - it must fault the session's own background
        // repeat-attack loop task AND promptly terminate the session's own live RunAsync (a real
        // defect must not leave a connected player session alive indefinitely with a dead attack
        // scheduler - see RunRepeatAttackLoopAsync's own terminal-failure catch, which cancels this
        // session's own _sessionCancellation and rethrows). The observable production behavior is
        // therefore: send attack -> scripted defect -> repeat-attack loop faults -> session
        // cancellation is triggered -> RunAsync itself terminates/faults promptly - proven here
        // directly against `run` (RunAsync's own task), with NO external DisposeAsync call required
        // to finally observe it (a misclassified "transient" retry would instead have this test time
        // out waiting for `run` to complete, since the session would stay alive indefinitely).
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType(exceptionType, thrown);

        // Exactly ONE call happened - a misclassified "transient" retry would have kept calling
        // TryMarkMonsterDeadAsync repeatedly instead of faulting the loop on the very first attempt.
        Assert.Equal(1, fakeWorld.TryMarkMonsterDeadCallCount);

        // Local combat state must remain untouched - the defect happened before any HP mutation
        // could occur (TryMarkMonsterDeadAsync throws BEFORE CommitConfirmedDeath is ever reached).
        var key = new MonsterCombatKey(target.Map, epoch, target.ActorId, incarnation);
        Assert.True(combatState.TryGet(key, out var state));
        Assert.Equal(1u, state.CurrentHp);

        // `run` has already faulted and been awaited above (proving RunAsync itself terminated
        // promptly without needing an external DisposeAsync call) - DisposeAsync would simply
        // re-observe/rethrow the SAME already-surfaced fault via StopCoreAsync's own
        // Task.WhenAll(loops), so it is deliberately not called again here.
        client.Close();
    }

    // The legitimate transient case (IOException, already covered end-to-end in
    // MapClientSessionTransientWorldRpcFailureTests.cs) must re-arm NextAttackAt to a FUTURE time,
    // never leave an already-due RepeatAttackState that could spin the loop immediately - proven here
    // directly against RearmAfterTransientFailureAsync's own observable effect: the SECOND
    // TryMarkMonsterDeadAsync attempt must not happen essentially instantaneously after the first.
    [Fact]
    public async Task LethalHit_TransientFailure_DoesNotHotLoop_RetryIsPacedByOrdinaryAttackCadence()
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
        var incarnation = new WorldMonsterIncarnationId(target.IncarnationId.Value);
        combatState.Register(target.Map, epoch, target.ActorId, incarnation, maxHp: 1);
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(), combatState);
        var monsterProjections = WorldMonsterProjectionTestHelper.SeedProjection(target.Map, epoch, combatState, registry.AllInstances);

        var gameplayPersistence = new RecordingGameplayStatePersistence(StrongAttacker());
        // Always throws IOException (transient) - if the caller failed to re-arm NextAttackAt to a
        // FUTURE time, the loop would call TryMarkMonsterDeadAsync as fast as the CPU allows.
        var fakeWorld = new FakeCombatWorldRuntime { ThrowTransientTryMarkMonsterDeadCount = int.MaxValue };

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

        await stream.WriteAsync(AttackPacket(actorId));

        // Wait for the FIRST attempt, then measure how long it takes to observe a SECOND - a hot loop
        // would produce many calls within milliseconds; a correctly-paced retry uses the ordinary
        // attack-delay cadence (hundreds of ms at minimum for a real weapon/stat combination).
        var firstSeenDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (fakeWorld.TryMarkMonsterDeadCallCount < 1 && DateTime.UtcNow < firstSeenDeadline) await Task.Delay(5);
        Assert.True(fakeWorld.TryMarkMonsterDeadCallCount >= 1);
        var firstObservedAt = DateTime.UtcNow;

        var secondSeenDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (fakeWorld.TryMarkMonsterDeadCallCount < 2 && DateTime.UtcNow < secondSeenDeadline) await Task.Delay(5);
        Assert.True(fakeWorld.TryMarkMonsterDeadCallCount >= 2, "Expected the loop to keep retrying (not die) after a transient failure.");
        var elapsed = DateTime.UtcNow - firstObservedAt;

        // A genuine hot loop would produce the second call within a handful of milliseconds; the
        // ordinary attack cadence for this fixture is on the order of hundreds of milliseconds at
        // minimum - a generous lower bound well below any real cadence value still proves this is not
        // spinning immediately.
        Assert.True(elapsed > TimeSpan.FromMilliseconds(50), $"Expected the retry to be paced by the ordinary attack cadence, not a hot loop - observed only {elapsed.TotalMilliseconds}ms between attempts.");

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
