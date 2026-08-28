using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Testing;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionStatusExpirationTests
{
    private const int BlessingDurationMs = 240000;

    [Fact]
    public async Task BlessingRemainsActiveBeforeItsDeadline()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();
        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs - 1));

        Assert.True(harness.Session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.Equal(0, harness.Client.Client.Available);
    }

    [Fact]
    public async Task BlessingExpiresAtItsDeadlineAndSendsStatusEndPacket()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs));

        Assert.False(harness.Session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        var end = await harness.ReadExact(9);
        Assert.Equal(PacketConstants.ZcMsgStateChange, BinaryPrimitives.ReadInt16LittleEndian(end));
        Assert.Equal(IroStatusEffectPackets.EfstBlessing, BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(2)));
        Assert.Equal(harness.AccountId, BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(4)));
        Assert.Equal(0, end[8]);
    }

    [Fact]
    public async Task IncreaseAgiExpiresAtItsDeadlineAndSendsStatusEndPacket()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.IncreaseAgi, BlessingDurationMs, 10, default);
        await harness.DrainIncreaseAgiActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs));

        Assert.False(harness.Session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.IncreaseAgi, out _));
        var end = await harness.ReadExact(9);
        Assert.Equal(PacketConstants.ZcMsgStateChange, BinaryPrimitives.ReadInt16LittleEndian(end));
        Assert.Equal(IroStatusEffectPackets.EfstIncAgi, BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(2)));
    }

    [Fact]
    public async Task ExpirationRemovesStatusFromEffectiveServerStats()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs));
        await harness.ReadExact(9); // 0x0196
        await harness.ReadExact(14); await harness.ReadExact(14); await harness.ReadExact(14); // STR/INT/DEX revert

        var effective = harness.Session.StatusEffects.Recalculate(harness.Session.GameplayState!.State);
        Assert.Equal(harness.Session.GameplayState!.State.Strength, effective.Strength);
    }

    [Fact]
    public async Task BlessingExpiryRevertsStrIntDexToBaseEffectiveValues()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs));
        await harness.ReadExact(9); // 0x0196

        foreach (var expectedType in new ushort[] { IroStatusEffectPackets.SpStr, IroStatusEffectPackets.SpInt, IroStatusEffectPackets.SpDex })
        {
            var stat = await harness.ReadExact(14);
            Assert.Equal(PacketConstants.ZcCoupleStatus, BinaryPrimitives.ReadInt16LittleEndian(stat));
            Assert.Equal(expectedType, BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(2)));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(6))); // base STR/INT/DEX == 1
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(10))); // plus reverted to 0
        }
        Assert.Equal(0, harness.Client.Client.Available);
    }

    [Fact]
    public async Task IncreaseAgiExpiryRevertsAgiByTwelveForCaptainVal1Ten()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.IncreaseAgi, BlessingDurationMs, 10, default);
        await harness.DrainIncreaseAgiActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs));
        await harness.ReadExact(9); // 0x0196

        var stat = await harness.ReadExact(14);
        Assert.Equal(PacketConstants.ZcCoupleStatus, BinaryPrimitives.ReadInt16LittleEndian(stat));
        Assert.Equal(IroStatusEffectPackets.SpAgi, BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(6))); // base AGI == 1
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(10))); // plus reverted to 0 (was +12)
        Assert.Equal(0, harness.Client.Client.Available);
    }

    [Fact]
    public async Task RefreshBeforeExpirationPreventsOldDeadlineFromExpiringStatus()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs - 5000));
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 3, default);
        await harness.DrainBlessingActivation();

        // The OLD deadline (original activation + 240000ms) has now passed, but the refresh
        // moved ExpiresAt forward, so nothing should have expired and no 0x0196/0x0141 should
        // have been sent for it.
        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(5001));

        Assert.True(harness.Session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out var status));
        Assert.Equal(3, status.Val1);
        Assert.Equal(0, harness.Client.Client.Available);
    }

    [Fact]
    public async Task RefreshedStatusExpiresAtTheNewDeadline()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs - 5000));
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, 100000, 3, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(99999));
        Assert.True(harness.Session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.Equal(0, harness.Client.Client.Available);

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(2));
        Assert.False(harness.Session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        await harness.ReadExact(9); // 0x0196 fires at the NEW deadline
    }

    [Fact]
    public async Task TwoSimultaneousStatusesExpireCorrectlyWithoutCorruptingEffectiveValues()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.IncreaseAgi, BlessingDurationMs, 10, default);
        await harness.DrainIncreaseAgiActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs));

        Assert.False(harness.Session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.False(harness.Session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.IncreaseAgi, out _));

        // Both statuses share the same deadline, so both are processed by the same
        // ExpireDue() batch: two 0x0196 (order not asserted - a set), then the union of
        // changed stats (STR/INT/DEX from Blessing, AGI from IncreaseAgi).
        var ends = new HashSet<ushort>();
        for (var i = 0; i < 2; i++)
        {
            var end = await harness.ReadExact(9);
            Assert.Equal(PacketConstants.ZcMsgStateChange, BinaryPrimitives.ReadInt16LittleEndian(end));
            ends.Add(BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(2)));
        }
        Assert.Equal(new HashSet<ushort> { IroStatusEffectPackets.EfstBlessing, IroStatusEffectPackets.EfstIncAgi }, ends);

        var statTypes = new HashSet<uint>();
        for (var i = 0; i < 4; i++)
        {
            var stat = await harness.ReadExact(14);
            Assert.Equal(PacketConstants.ZcCoupleStatus, BinaryPrimitives.ReadInt16LittleEndian(stat));
            statTypes.Add(BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(2)));
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(10)));
        }
        Assert.Equal(new HashSet<uint> { IroStatusEffectPackets.SpStr, IroStatusEffectPackets.SpInt, IroStatusEffectPackets.SpDex, IroStatusEffectPackets.SpAgi }, statTypes);
        Assert.Equal(0, harness.Client.Client.Available);

        var effective = harness.Session.StatusEffects.Recalculate(harness.Session.GameplayState!.State);
        Assert.Equal(harness.Session.GameplayState!.State.Strength, effective.Strength);
        Assert.Equal(harness.Session.GameplayState!.State.Agility, effective.Agility);
    }

    [Fact]
    public async Task TemporaryStatusExpirationPerformsZeroGameplayStatePersistence()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StartStatusAndWaitForRescheduleAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs));
        await harness.ReadExact(9); await harness.ReadExact(14); await harness.ReadExact(14); await harness.ReadExact(14);

        Assert.Equal(0, harness.Persistence.Updates);
    }

    [Fact]
    public async Task SessionDisposalTerminatesExpirationSchedulerCleanly()
    {
        var harness = await Harness.CreateAsync();
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        // StopAsync joins the scheduler before returning, so by the time it completes the loop is
        // provably no longer running - not merely cancellation-requested.
        await harness.Session.StopAsync();

        // Advancing the clock past the deadline after shutdown must not throw or hang -
        // the scheduler already exited instead of writing to a disposed stream.
        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs));
        harness.Listener.Stop();
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required MapClientSession Session;
        public required TcpClient Client;
        public required TcpListener Listener;
        public required ControllableTimeProvider Clock;
        public required ScriptContext Context;
        public required RecordingPersistence Persistence;
        public required uint AccountId;

        public static async Task<Harness> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var client = new TcpClient();
            var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port);
            var server = await listener.AcceptTcpClientAsync();
            await connecting;

            var clock = new ControllableTimeProvider();
            var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
            const uint accountId = 7;
            var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false,
                accountId: accountId, gameplayStatePersistence: persistence, timeProvider: clock);
            await session.CompleteIroAuthenticationAsync(new(accountId, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
            var bootstrap = new byte[29];
            await client.GetStream().ReadExactlyAsync(bootstrap);
            var skillListHeader = new byte[4]; await client.GetStream().ReadExactlyAsync(skillListHeader); var skillListBody = new byte[BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4]; await client.GetStream().ReadExactlyAsync(skillListBody);

            return new Harness
            {
                Session = session,
                Client = client,
                Listener = listener,
                Clock = clock,
                Context = new ScriptContext(session, "npc:test", 1, "Test", null),
                Persistence = persistence,
                AccountId = accountId,
            };
        }

        public Task<byte[]> ReadExact(int length) => MapClientSessionStatusExpirationTests.ReadExact(Client.GetStream(), length);

        public async Task DrainBlessingActivation()
        {
            await ReadExact(29); await ReadExact(14); await ReadExact(14); await ReadExact(14);
        }

        public async Task DrainIncreaseAgiActivation()
        {
            await ReadExact(29); await ReadExact(14);
        }

        // Captures the registration generation BEFORE triggering StartStatusAsync, then waits for
        // the expiration scheduler's resulting reschedule to actually register its new deadline -
        // deterministically, not via a fixed real-time settle. StartStatusAsync's own signal release
        // is fire-and-forget from the scheduler's perspective (see MapClientSession's own doc
        // comment on _statusExpirationSignal), so without this a subsequent AdvanceAsync could race
        // ahead of the scheduler's wake-and-reschedule.
        public async Task StartStatusAndWaitForRescheduleAsync(ushort statusId, int durationMilliseconds, int val1, CancellationToken cancellationToken)
        {
            var generation = Clock.RegistrationGeneration;
            await Context.StartStatusAsync(statusId, durationMilliseconds, val1, cancellationToken);
            await Clock.WaitForRegistrationAfterAsync(generation).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            await Session.StopAsync();
            Client.Dispose();
            Listener.Stop();
        }
    }

    private static async Task<byte[]> ReadExact(NetworkStream stream, int length)
    {
        var data = new byte[length];
        await stream.ReadExactlyAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return data;
    }

    private sealed class RecordingPersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        private CharacterGameplayState _state = state;
        public int Updates { get; private set; }
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(_state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            Updates++;
            if (expected.Version != _state.Version) return Task.FromResult<CharacterGameplayState?>(null);
            _state = updated with { Version = expected.Version + 1 };
            return Task.FromResult<CharacterGameplayState?>(_state);
        }
    }
}
