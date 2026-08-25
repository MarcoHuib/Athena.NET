using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

// Deterministic controllable TimeProvider: GetUtcNow() only advances on explicit Advance()
// calls, and Advance() also fires any Task.Delay(..., this, ...) timers whose due time has
// been crossed - this is what lets MapClientSession's expiration scheduler (built on
// Task.Delay(delay, TimeProvider, cancellationToken), which internally calls CreateTimer) be
// driven deterministically in tests without real 240-second waits or a new package dependency.
internal sealed class ControllableTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly List<ScheduledCallback> _callbacks = [];

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ScheduledTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public async Task AdvanceAsync(TimeSpan delta)
    {
        List<ScheduledCallback> due;
        lock (_gate)
        {
            _now += delta;
            due = [.. _callbacks.Where(c => c.DueAt <= _now)];
            _callbacks.RemoveAll(c => c.DueAt <= _now);
        }
        foreach (var callback in due) callback.Invoke();
        // A short settle so synchronous-looking assertions right after AdvanceAsync (e.g.
        // "nothing was sent") are stable; tests that expect a packet rely on ReadExact's own
        // generous timeout (it blocks until data arrives), not on this loop, since the write
        // chain's length varies (single vs. simultaneous expirations) and CI/parallel test
        // load can slow scheduling arbitrarily.
        for (var i = 0; i < 20; i++)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    // Waits (bounded, real time) until at least one timer is registered - i.e. until the
    // scheduler loop has reacted to a wake signal and called CreateTimer for its new deadline.
    // Needed before the next AdvanceAsync when the wake was triggered by something that does
    // NOT itself await the scheduler's reschedule (StartStatusAsync releases the signal and
    // returns; the scheduler's wake-and-reschedule runs concurrently on the background task).
    public async Task SettleAsync()
    {
        for (var i = 0; i < 200; i++)
        {
            lock (_gate) { if (_callbacks.Count > 0) return; }
            await Task.Delay(5);
        }
    }

    private void Schedule(ScheduledCallback callback)
    {
        lock (_gate)
        {
            if (callback.DueAt <= _now) { callback.Invoke(); return; }
            _callbacks.Add(callback);
        }
    }

    private void Cancel(ScheduledCallback callback) { lock (_gate) _callbacks.Remove(callback); }

    private sealed record ScheduledCallback(DateTimeOffset DueAt, TimerCallback Callback, object? State)
    {
        public void Invoke() => Callback(State);
    }

    private sealed class ScheduledTimer(ControllableTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private ScheduledCallback? _current;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_current is not null) owner.Cancel(_current);
            if (dueTime == Timeout.InfiniteTimeSpan) { _current = null; return true; }
            _current = new ScheduledCallback(owner.GetUtcNow() + dueTime, callback, state);
            owner.Schedule(_current);
            return true;
        }

        public void Dispose() { if (_current is not null) owner.Cancel(_current); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

public sealed class MapClientSessionStatusExpirationTests
{
    private const int BlessingDurationMs = 240000;

    [Fact]
    public async Task BlessingRemainsActiveBeforeItsDeadline()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs - 1));

        Assert.True(harness.Session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.Equal(0, harness.Client.Client.Available);
    }

    [Fact]
    public async Task BlessingExpiresAtItsDeadlineAndSendsStatusEndPacket()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
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
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.IncreaseAgi, BlessingDurationMs, 10, default);
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
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
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
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
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
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.IncreaseAgi, BlessingDurationMs, 10, default);
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
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs - 5000));
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 3, default);
        await harness.DrainBlessingActivation();
        await harness.Clock.SettleAsync();

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
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();

        await harness.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(BlessingDurationMs - 5000));
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, 100000, 3, default);
        await harness.DrainBlessingActivation();
        // Let the scheduler's background wake-and-reschedule (triggered by StartStatusAsync's
        // signal release, but not itself awaited by it) actually register the new deadline's
        // timer before advancing the clock again - otherwise AdvanceAsync can race ahead of
        // the reschedule and the still-in-flight old-deadline delay computation.
        await harness.Clock.SettleAsync();

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
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
        await harness.DrainBlessingActivation();
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.IncreaseAgi, BlessingDurationMs, 10, default);
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
        await harness.Context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, BlessingDurationMs, 10, default);
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

        harness.Session.Dispose();

        // Advancing the clock past the deadline after disposal must not throw or hang -
        // the scheduler observed cancellation and exited instead of writing to a disposed stream.
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

        public ValueTask DisposeAsync()
        {
            Session.Dispose();
            Client.Dispose();
            Listener.Stop();
            return ValueTask.CompletedTask;
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
