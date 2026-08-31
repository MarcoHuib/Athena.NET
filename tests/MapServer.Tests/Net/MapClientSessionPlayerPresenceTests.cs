using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionPlayerPresenceTests
{
    [Fact]
    public async Task RagnarokMovementRequest_FlowsThroughProtocolIndependentWorldAuthority()
    {
        var runtime = new RecordingWorldRuntime();
        var players = new PlayerPresenceRegistry();
        var coordinator = new PlayerVisibilityCoordinator(players);
        await using var session = await ConnectAsync(30, 130, "Mover", 100, 100, players, coordinator, runtime);
        await EnterWorldAsync(session);
        await runtime.WaitForRegistrationsAsync(1);

        await session.Stream.WriteAsync(BuildMovementRequest(102, 101));
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(await ReadExactAsync(session.Stream, 12)));
        await runtime.WaitForMovementsAsync(1);

        var command = Assert.Single(runtime.Movements);
        Assert.Equal("prontera", command.MapId);
        Assert.Equal((ushort)100, command.FromX);
        Assert.Equal((ushort)100, command.FromY);
        Assert.Equal((ushort)102, command.DestinationX);
        Assert.Equal((ushort)101, command.DestinationY);
        Assert.Equal(runtime.Registrations[0].PresenceId, command.PresenceId);
    }

    [Fact]
    public async Task RepeatedWorldEntry_ReusesPresenceId_AndCleanupUsesSameIdentity()
    {
        var runtime = new RecordingWorldRuntime();
        var players = new PlayerPresenceRegistry();
        var coordinator = new PlayerVisibilityCoordinator(players);
        await using var session = await ConnectAsync(10, 110, "Retry", 100, 100, players, coordinator, runtime);

        await EnterWorldAsync(session);
        await runtime.WaitForRegistrationsAsync(1);
        // Models a registration replay after the first response was not observed by the caller.
        await EnterWorldAsync(session);
        await runtime.WaitForRegistrationsAsync(2);

        Assert.NotEqual(Guid.Empty, runtime.Registrations[0].PresenceId);
        Assert.Equal(runtime.Registrations[0].PresenceId, runtime.Registrations[1].PresenceId);

        await session.Session.StopAsync();
        await runtime.WaitForUnregistrationsAsync(1);
        Assert.Equal(runtime.Registrations[0].PresenceId, runtime.Unregistrations[0].PresenceId);
    }

    [Fact]
    public async Task IndependentWorldPresenceLifecycle_ReceivesNewPresenceId()
    {
        var runtime = new RecordingWorldRuntime();
        var players = new PlayerPresenceRegistry();
        var coordinator = new PlayerVisibilityCoordinator(players);

        await using (var first = await ConnectAsync(20, 120, "First", 100, 100, players, coordinator, runtime))
        {
            await EnterWorldAsync(first);
            await runtime.WaitForRegistrationsAsync(1);
            await first.Session.StopAsync();
            await runtime.WaitForUnregistrationsAsync(1);
        }

        await using (var second = await ConnectAsync(20, 120, "Second", 100, 100, players, coordinator, runtime))
        {
            await EnterWorldAsync(second);
            await runtime.WaitForRegistrationsAsync(2);
            Assert.NotEqual(runtime.Registrations[0].PresenceId, runtime.Registrations[1].PresenceId);
        }
    }

    [Fact]
    public async Task TwoRealSessions_DiscoverMoveLookInfoAndDisconnectReciprocally()
    {
        var players = new PlayerPresenceRegistry();
        var coordinator = new PlayerVisibilityCoordinator(players);
        await using var a = await ConnectAsync(1, 101, "Alice", 100, 100, players, coordinator);
        await EnterWorldAsync(a);
        // EnterWorldAsync only waits for the self-weapon/inventory packets HandlePacketAsync
        // sends BEFORE calling EnterPlayerWorldAsync (the actual PlayerPresenceRegistry insert) -
        // see the pinned ordering comment at that 0x007D handler. Without this, connecting B can
        // race ahead of A's own registration, and whichever session's insert wins the race becomes
        // the "existing" side (0x09FF) while the other becomes "newly spawned" (0x09FE) - flipping
        // the two assertions below nondeterministically instead of a real production bug.
        await WaitForRegistrationAsync(players, 1);

        await using var b = await ConnectAsync(2, 102, "Bob", 105, 105, players, coordinator);
        await EnterWorldAsync(b);
        await WaitForRegistrationAsync(players, 2);

        var aSeesB = await ReadVariablePacketAsync(a.Stream);
        Assert.Equal((short)0x09fe, BinaryPrimitives.ReadInt16LittleEndian(aSeesB));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(aSeesB.AsSpan(5)));
        Assert.Equal("Bob", Encoding.ASCII.GetString(aSeesB.AsSpan(83)));

        var bSeesA = await ReadVariablePacketAsync(b.Stream);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(bSeesA));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bSeesA.AsSpan(5)));
        Assert.Equal("Alice", Encoding.ASCII.GetString(bSeesA.AsSpan(84)));

        await a.Stream.WriteAsync(BuildMovementRequest(101, 100));
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(await ReadExactAsync(a.Stream, 12)));
        var movement = await ReadVariablePacketAsync(b.Stream);
        Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(movement));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(movement.AsSpan(5)));

        await a.Stream.WriteAsync(new byte[] { 0x61, 0x03, 0x02, 0x00, 0x01, 0x7f });
        Assert.Equal(Convert.FromHexString("9C0001000000020001"), await ReadExactAsync(b.Stream, 9));

        var infoRequest = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(infoRequest, 0x0368);
        BinaryPrimitives.WriteUInt32LittleEndian(infoRequest.AsSpan(2), 1);
        infoRequest[6] = 0xe3; // capture-proven opaque; must not affect lookup.
        await b.Stream.WriteAsync(infoRequest);
        var info = await ReadExactAsync(b.Stream, 106);
        Assert.Equal((short)0x0a30, BinaryPrimitives.ReadInt16LittleEndian(info));
        Assert.Equal("Alice", Encoding.ASCII.GetString(info.AsSpan(6, 24)).TrimEnd('\0'));

        await a.Session.StopAsync();
        var vanish = await ReadExactAsync(b.Stream, 7);
        Assert.Equal(Convert.FromHexString("80000100000000"), vanish);
        Assert.False(players.TryGetByActorId(1, out _));
    }

    private static async Task EnterWorldAsync(TestSession session)
    {
        await session.Stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExactAsync(session.Stream, 15); // 0x01D7 self weapon
        await ReadExactAsync(session.Stream, 6);  // inventory start
        await ReadExactAsync(session.Stream, 4);  // inventory end
    }

    private static async Task WaitForRegistrationAsync(PlayerPresenceRegistry players, uint actorId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!players.TryGetByActorId(actorId, out _))
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, timeout.Token);
        }
    }

    private static async Task<TestSession> ConnectAsync(uint accountId, uint charId, string name, ushort x, ushort y,
        PlayerPresenceRegistry players, PlayerVisibilityCoordinator coordinator, IWorldRuntime? worldRuntime = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var client = new TcpClient();
        var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port);
        var server = await listener.AcceptTcpClientAsync();
        await connecting;

        var state = new CharacterGameplayState(charId, 1, 0, 10, 5, 0, 0, 100, 20, 100, 20, 0, 0, 9, 9, 9, 9, 9, 9);
        // iroAuthenticated: true is required here (not just "already authenticated"): it also
        // flips the internal _iroAuthRequested flag, which HandlePacketAsync's 0x007D branch
        // gates on to choose the iRO bootstrap path (0x01D7 self weapon + inventory list) over
        // the legacy CZ_ENTER path. In production that flag is set by HandleIroMapAuthAsync
        // before HandleAuthOk ever calls CompleteIroAuthenticationAsync; this test instead calls
        // CompleteIroAuthenticationAsync directly, so the constructor is the only place left to
        // set it.
        var session = new MapClientSession((int)accountId, server,
            new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), true,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            players: players, playerVisibility: coordinator,
            visibilityOptions: WorldVisibilityOptions.Default,
            distributedWorld: worldRuntime);
        var auth = new MapAuthOkData(accountId, charId, 1, 2, 0, 0, false, "prontera", x, y, 0, 0, 1, name,
            HairStyle: 4, HairColor: 2, ClothesColor: 1);
        await session.CompleteIroAuthenticationAsync(auth);
        var stream = client.GetStream();
        await ConsumeBootstrapAsync(stream);
        var runTask = session.RunAsync(CancellationToken.None);
        return new TestSession(client, server, stream, session, runTask);
    }

    private static async Task ConsumeBootstrapAsync(Stream stream)
    {
        await ReadExactAsync(stream, 29);
        var header = await ReadExactAsync(stream, 4);
        await ReadExactAsync(stream, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) - 4);
    }

    private static byte[] BuildMovementRequest(ushort x, ushort y)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x035f);
        packet[2] = (byte)(x >> 2);
        packet[3] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        packet[4] = (byte)(y << 4);
        packet[5] = 0x44;
        return packet;
    }

    private static async Task<byte[]> ReadVariablePacketAsync(Stream stream)
    {
        var header = await ReadExactAsync(stream, 4);
        var packet = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2))];
        header.CopyTo(packet, 0);
        (await ReadExactAsync(stream, packet.Length - 4)).CopyTo(packet, 4);
        return packet;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private sealed class RecordingWorldRuntime : IWorldRuntime
    {
        private readonly Lock _gate = new();
        private readonly List<WorldPlayerPresence> _registrations = [];
        private readonly List<(uint CharacterId, Guid PresenceId)> _unregistrations = [];
        private readonly List<WorldMovementCommand> _movements = [];

        public IReadOnlyList<WorldPlayerPresence> Registrations { get { lock (_gate) return _registrations.ToArray(); } }
        public IReadOnlyList<(uint CharacterId, Guid PresenceId)> Unregistrations { get { lock (_gate) return _unregistrations.ToArray(); } }
        public IReadOnlyList<WorldMovementCommand> Movements { get { lock (_gate) return _movements.ToArray(); } }

        public Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var status = _registrations.Any(existing => existing.CharacterId == presence.CharacterId && existing.PresenceId == presence.PresenceId)
                    ? WorldPresenceRegistrationStatus.AlreadyRegistered
                    : WorldPresenceRegistrationStatus.Registered;
                _registrations.Add(presence);
                return Task.FromResult(new WorldPresenceRegistration("test-partition", mapId, status, 1));
            }
        }

        public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _unregistrations.Add((characterId, presenceId));
                return Task.FromResult(new WorldPresenceUnregistration("test-partition", mapId, WorldPresenceUnregistrationStatus.Removed, 0));
            }
        }

        public Task WaitForRegistrationsAsync(int count) => WaitUntilAsync(() => Registrations.Count >= count);
        public Task WaitForUnregistrationsAsync(int count) => WaitUntilAsync(() => Unregistrations.Count >= count);

        public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _movements.Add(command);
                var registered = _registrations.Last();
                return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Moved,
                    registered with { X = command.DestinationX, Y = command.DestinationY }));
            }
        }

        public Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldTransferResult(WorldTransferStatus.Completed, WorldTransferType.SamePartition, null));

        public Task WaitForMovementsAsync(int count) => WaitUntilAsync(() => Movements.Count >= count);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(5, timeout.Token);
            }
        }
    }

    private sealed class TestSession(TcpClient client, TcpClient server, NetworkStream stream, MapClientSession session, Task runTask) : IAsyncDisposable
    {
        public TcpClient Client { get; } = client;
        public TcpClient Server { get; } = server;
        public NetworkStream Stream { get; } = stream;
        public MapClientSession Session { get; } = session;
        public Task RunTask { get; } = runTask;
        public async ValueTask DisposeAsync()
        {
            await Session.StopAsync();
            Client.Dispose();
            Server.Dispose();
            try { await RunTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }
}
