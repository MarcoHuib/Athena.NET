using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 6 hardening, item 6: a transient UpdatePresenceLifeStateAsync failure right after a real
// local Alive->Dead player transition must not leave the player locally Dead while World
// indefinitely still reports IsAlive=true - MapClientSession.TryReconcilePendingLifeStateAsync
// (called by ApplyIncomingMobBasicAttackAsync immediately after the transition, AND by
// MapTcpServer's own tick loop every tick regardless of whether a new transition happened) must
// keep retrying a pending update until it durably succeeds or is retired by a StalePresence result.
public sealed class MapClientSessionPendingLifeStateReconciliationTests
{
    private const uint AccountId = 21;
    private const uint CharId = 23;

    private static CharacterGameplayState AliveState(uint currentHp = 10) => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: currentHp, CurrentSp: 10, MaxHp: 10, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 1, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1);

    private sealed class RecordingGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        private CharacterGameplayState _state = state;
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(_state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            _state = updated;
            return Task.FromResult<CharacterGameplayState?>(updated);
        }
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

    // _presenceId (required for the pending-life-state mechanism under test) is only set inside
    // EnterPlayerWorldAsync, itself only reached after this session's OWN packet loop observes the
    // client's map-loaded packet (0x007D) - CompleteIroAuthenticationAsync alone does not reach it.
    // Drives the session through the full bootstrap burst + map-loaded packet, mirroring
    // MapClientSessionNonLethalAttackFailClosedTests.cs's own established wiring exactly, so
    // _presenceId is genuinely populated before this helper returns.
    private static async Task<(TcpClient Client, MapClientSession Session, Task RunTask)> SetupAsync(FakeCombatWorldRuntime fakeWorld, CharacterGameplayState initialState)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var combatState = new MonsterCombatStateStore();
        var gameplayPersistence = new RecordingGameplayStatePersistence(initialState);
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsterProjections: new MonsterFeedProjectionRegistry(),
            combat: new MonsterCombatCoordinator(new QuestDropResolver([]), new Athena.Net.MapServer.Gameplay.Rules.Renewal.RenewalBasicAttackRules(), combatState),
            combatState: combatState, distributedWorld: fakeWorld);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream); // 0x0B32 skill list

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa }); // Client map-loaded packet - reaches EnterPlayerWorldAsync, which sets _presenceId.
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        await ReadExact(stream, 4);  // 0x0B0B inventoryEnd

        return (client, session, run);
    }

    [Fact]
    public async Task TransientFailureImmediatelyAfterDeathTransition_RetriedOnNextTickCall_EventuallySucceeds()
    {
        var fakeWorld = new FakeCombatWorldRuntime { ThrowTransientFailureCount = 2 };
        var (client, session, run) = await SetupAsync(fakeWorld, AliveState(currentHp: 5));
        using var _ = client;

        // A real local Alive->Dead transition - the FIRST UpdatePresenceLifeStateAsync attempt
        // (made synchronously right after the mutation) fails transiently.
        var result = await session.ApplyIncomingMobBasicAttackAsync(damage: 999, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(0u, result!.Value.HpAfter);
        Assert.Equal(1, fakeWorld.UpdatePresenceLifeStateCallCount); // The one attempt that just failed.

        // Simulate MapTcpServer's own tick-loop retry (called every tick regardless of whether a NEW
        // transition happened) - the second attempt ALSO fails transiently (ThrowTransientFailureCount
        // was 2), the third succeeds.
        await session.TryReconcilePendingLifeStateAsync(CancellationToken.None);
        Assert.Equal(2, fakeWorld.UpdatePresenceLifeStateCallCount);

        await session.TryReconcilePendingLifeStateAsync(CancellationToken.None);
        Assert.Equal(3, fakeWorld.UpdatePresenceLifeStateCallCount); // Third attempt succeeds.

        // A FOURTH call must be a complete no-op (nothing pending anymore) - proving the pending
        // state was cleared on success, never spamming the RPC once nothing is outstanding.
        await session.TryReconcilePendingLifeStateAsync(CancellationToken.None);
        Assert.Equal(3, fakeWorld.UpdatePresenceLifeStateCallCount);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NoTransitionHasEverHappened_TickCall_NeverCallsTheRpcAtAll()
    {
        var fakeWorld = new FakeCombatWorldRuntime();
        var (client, session, run) = await SetupAsync(fakeWorld, AliveState());
        using var _ = client;

        await session.TryReconcilePendingLifeStateAsync(CancellationToken.None);
        await session.TryReconcilePendingLifeStateAsync(CancellationToken.None);

        Assert.Equal(0, fakeWorld.UpdatePresenceLifeStateCallCount);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StalePresenceResult_RetiresThePendingUpdate_NeverRetriedAgain()
    {
        var fakeWorld = new FakeCombatWorldRuntime { UpdatePresenceLifeStateStatusOverride = WorldPresenceLifeStateStatus.StalePresence };
        var (client, session, run) = await SetupAsync(fakeWorld, AliveState(currentHp: 5));
        using var _ = client;

        await session.ApplyIncomingMobBasicAttackAsync(damage: 999, CancellationToken.None);
        Assert.Equal(1, fakeWorld.UpdatePresenceLifeStateCallCount); // First attempt observes StalePresence.

        // A replacement session/presence has already superseded this one - retrying could never
        // succeed, so the pending update must be retired outright, never retried again.
        await session.TryReconcilePendingLifeStateAsync(CancellationToken.None);
        await session.TryReconcilePendingLifeStateAsync(CancellationToken.None);

        Assert.Equal(1, fakeWorld.UpdatePresenceLifeStateCallCount);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
