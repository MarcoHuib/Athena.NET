using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

// End-to-end CharServer-side coverage of the live "server-selection shows 0 online players" fix: a
// REAL MapServerSession receives MapSendUserCount over its real authenticated wire, updates the real
// MapServerRegistry, and forwards the resulting aggregate to a REAL LoginServerConnector's own wire
// send (captured here via a loopback socket standing in for LoginServer, injected through the same
// narrow reflection seam this project already uses elsewhere for a private field with no production
// setter - see MapTcpServerRunAsyncSupervisionTests' own doc comment for the precedent).
public sealed class MapServerSessionUserCountTests : IDisposable
{
    private const int NameLength = 24;
    private const int ServiceNonceLength = 32;
    private const int ServiceProofLength = 32;
    private const string ServiceToken32 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; // 32 raw bytes once base64-decoded padding aside - see BuildToken.

    private readonly List<Fixture> _fixtures = [];

    public void Dispose()
    {
        foreach (var fixture in _fixtures) fixture.Dispose();
    }

    private static string BuildToken(byte seed) => Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + seed)).ToArray());

    private Fixture CreateFixture() => Track(Fixture.Create(BuildToken(0)));
    private Fixture Track(Fixture fixture) { _fixtures.Add(fixture); return fixture; }

    [Fact]
    public async Task OneAuthenticatedMapServer_ReportsOnePlayer_LoginServerReceivesOne()
    {
        var fixture = CreateFixture();
        await fixture.CompleteHandshakeAsync();

        await fixture.SendUserCountAsync(1);

        var packet = await fixture.ReadLoginServerPacketAsync();
        Assert.Equal(PacketConstants.LcUserCount, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(0, 2)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4)));
        Assert.Equal(1, fixture.Registry.TotalUsers);
    }

    [Fact]
    public async Task TwoAuthenticatedPlayersOnOneMapServer_LoginServerReceivesTwo()
    {
        var fixture = CreateFixture();
        await fixture.CompleteHandshakeAsync();

        await fixture.SendUserCountAsync(2);

        var packet = await fixture.ReadLoginServerPacketAsync();
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4)));
    }

    [Fact]
    public async Task ZeroPlayers_LoginServerReceivesZero()
    {
        var fixture = CreateFixture();
        await fixture.CompleteHandshakeAsync();

        await fixture.SendUserCountAsync(0);

        var packet = await fixture.ReadLoginServerPacketAsync();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4)));
    }

    [Fact]
    public async Task UnauthenticatedMapServerConnection_UserCountPacketIsIgnored()
    {
        var fixture = CreateFixture();
        // Deliberately never completes the service handshake - MapSendUserCount must be rejected
        // exactly like every other post-auth packet this session accepts.
        await fixture.SendUserCountAsync(1);

        Assert.False(await fixture.TryReadLoginServerPacketAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(0, fixture.Registry.TotalUsers);
    }

    [Fact]
    public async Task FailedAuthentication_NeverContributesToTheCount()
    {
        var fixture = CreateFixture();
        await fixture.CompleteHandshakeAsync(expectSuccess: false, proofTokenBase64: BuildToken(99));

        await fixture.SendUserCountAsync(1);

        Assert.False(await fixture.TryReadLoginServerPacketAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(0, fixture.Registry.TotalUsers);
    }

    [Fact]
    public async Task TwoMapServerConnections_AreAggregated_NotOverwritten()
    {
        var registry = new MapServerRegistry();
        var a = CreateFixtureSharingRegistry(registry, sessionId: 1);
        var b = CreateFixtureSharingRegistry(registry, sessionId: 2);
        await a.CompleteHandshakeAsync();
        await b.CompleteHandshakeAsync();

        await a.SendUserCountAsync(2);
        var afterA = await a.ReadLoginServerPacketAsync();
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(afterA.AsSpan(2, 4)));

        await b.SendUserCountAsync(3);
        var afterB = await b.ReadLoginServerPacketAsync();
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(afterB.AsSpan(2, 4))); // 2 + 3.

        Assert.Equal(5, registry.TotalUsers);
    }

    [Fact]
    public async Task DisconnectingOneMapServer_TheOthersContributionSurvives()
    {
        var registry = new MapServerRegistry();
        var a = CreateFixtureSharingRegistry(registry, sessionId: 1);
        var b = CreateFixtureSharingRegistry(registry, sessionId: 2);
        await a.CompleteHandshakeAsync();
        await b.CompleteHandshakeAsync();
        await a.SendUserCountAsync(2);
        await a.ReadLoginServerPacketAsync();
        await b.SendUserCountAsync(3);
        await b.ReadLoginServerPacketAsync();

        a.DisconnectSessionOnly();

        Assert.Equal(3, registry.TotalUsers);
    }

    private Fixture CreateFixtureSharingRegistry(MapServerRegistry registry, int sessionId) =>
        Track(Fixture.Create(BuildToken((byte)sessionId), registry, sessionId));

    private sealed class Fixture : IDisposable
    {
        private readonly TcpListener _mapListener;
        private readonly TcpClient _mapTestClient;
        private readonly TcpClient _mapServerSide;
        private readonly TcpListener _loginListener;
        private readonly TcpClient _loginServerSideOfConnector; // The end LoginServerConnector itself writes to.
        private readonly TcpClient _loginTestClient; // Stands in for LoginServer, reads what CharServer sent.
        private readonly CancellationTokenSource _cts = new();
        private readonly MapServerSession _session;
        private readonly Task _runTask;
        private readonly string _proofToken;

        public MapServerRegistry Registry { get; }

        private Fixture(TcpListener mapListener, TcpClient mapTestClient, TcpClient mapServerSide, TcpListener loginListener,
            TcpClient loginServerSideOfConnector, TcpClient loginTestClient, MapServerSession session, MapServerRegistry registry, string proofToken)
        {
            _mapListener = mapListener; _mapTestClient = mapTestClient; _mapServerSide = mapServerSide;
            _loginListener = loginListener; _loginServerSideOfConnector = loginServerSideOfConnector; _loginTestClient = loginTestClient;
            _session = session; Registry = registry; _proofToken = proofToken;
            _runTask = _session.RunAsync(_cts.Token);
        }

        public static Fixture Create(string proofTokenBase64, MapServerRegistry? sharedRegistry = null, int sessionId = 1)
        {
            var mapListener = new TcpListener(IPAddress.Loopback, 0);
            mapListener.Start();
            var mapEndpoint = (IPEndPoint)mapListener.LocalEndpoint;
            var mapTestClient = new TcpClient();
            var mapConnectTask = mapTestClient.ConnectAsync(IPAddress.Loopback, mapEndpoint.Port);
            var mapServerSide = mapListener.AcceptTcpClient();
            mapConnectTask.GetAwaiter().GetResult();

            var loginListener = new TcpListener(IPAddress.Loopback, 0);
            loginListener.Start();
            var loginEndpoint = (IPEndPoint)loginListener.LocalEndpoint;
            var loginServerSideOfConnector = new TcpClient();
            var loginConnectTask = loginServerSideOfConnector.ConnectAsync(IPAddress.Loopback, loginEndpoint.Port);
            var loginTestClient = loginListener.AcceptTcpClient();
            loginConnectTask.GetAwaiter().GetResult();

            var configStore = new CharConfigStore(new CharConfig(), "unused.conf");
            var registry = sharedRegistry ?? new MapServerRegistry();
            var authManager = new MapAuthManager();
            var secrets = new SecretConfig { MapServerServiceToken = proofTokenBase64 };
            var tokenProvider = new MapServerServiceTokenProvider(secrets);
            var serviceAuth = new MapServiceAuthenticationService(tokenProvider);

            // A real LoginServerConnector, but with its private connection state injected directly
            // (via reflection - see this file's own doc comment) pointing at the loopback socket
            // above, so this test exercises the REAL TrySendUserCount wire write without needing the
            // full CharServer<->LoginServer service-auth handshake for every single test.
            var loginConnector = new LoginServerConnector(configStore, new CharServerServiceTokenProvider(new SecretConfig()));
            InjectLoginConnection(loginConnector, loginServerSideOfConnector.GetStream());

            var session = new MapServerSession(
                sessionId: sessionId,
                client: mapServerSide,
                configStore: configStore,
                registry: registry,
                loginConnector: loginConnector,
                authManager: authManager,
                dbFactory: () => null,
                serviceAuth: serviceAuth,
                prefetchedHeader: null);

            return new Fixture(mapListener, mapTestClient, mapServerSide, loginListener, loginServerSideOfConnector, loginTestClient, session, registry, proofTokenBase64);
        }

        private static void InjectLoginConnection(LoginServerConnector connector, NetworkStream stream)
        {
            var connectionType = typeof(LoginServerConnector).GetNestedType("LoginConnectionState", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("LoginServerConnector.LoginConnectionState not found - test seam broken by a rename.");
            var connectionInstance = Activator.CreateInstance(connectionType, stream)
                ?? throw new InvalidOperationException("Failed to construct LoginConnectionState.");
            var field = typeof(LoginServerConnector).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("LoginServerConnector._connection field not found - test seam broken by a rename.");
            field.SetValue(connector, connectionInstance);
        }

        public async Task CompleteHandshakeAsync(bool expectSuccess = true, string? proofTokenBase64 = null)
        {
            await SendHelloAsync();
            var challenge = await ReadMapExactAsync(2 + ServiceNonceLength);
            var nonce = challenge.AsSpan(2, ServiceNonceLength).ToArray();
            var hello = new MapServiceHelloInfo("TestMapServer", IPAddress.Loopback, 5121);
            var proof = MapServiceAuthProofCalculatorAccessor.ComputeProof(Convert.FromBase64String(proofTokenBase64 ?? _proofToken), hello, nonce);

            var proofPacket = new byte[2 + ServiceProofLength];
            BinaryPrimitives.WriteInt16LittleEndian(proofPacket.AsSpan(0, 2), PacketConstants.MapServiceAuthProof);
            proof.CopyTo(proofPacket, 2);
            await SendMapRawAsync(proofPacket);

            var result = await ReadMapExactAsync(3);
            if (expectSuccess) Assert.Equal((byte)0, result[2]);
            else Assert.NotEqual((byte)0, result[2]);
        }

        private Task SendHelloAsync()
        {
            var packet = new byte[2 + NameLength + 4 + 2 + 2];
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.MapServiceHello);
            var idBytes = Encoding.ASCII.GetBytes("TestMapServer");
            Buffer.BlockCopy(idBytes, 0, packet, 2, Math.Min(NameLength, idBytes.Length));
            IPAddress.Loopback.GetAddressBytes().CopyTo(packet, 2 + NameLength);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2 + NameLength + 4, 2), 5121);
            return SendMapRawAsync(packet);
        }

        public async Task SendUserCountAsync(uint users)
        {
            var packet = new byte[6];
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.MapSendUserCount);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), users);
            await SendMapRawAsync(packet);
            await Task.Delay(100); // Let the session's own RunAsync loop actually process the packet.
        }

        private async Task SendMapRawAsync(byte[] packet) => await _mapTestClient.GetStream().WriteAsync(packet);

        private async Task<byte[]> ReadMapExactAsync(int length)
        {
            var stream = _mapTestClient.GetStream();
            var buffer = new byte[length];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var total = 0;
            while (total < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, length - total), cts.Token);
                if (read == 0) throw new IOException($"Connection closed after reading {total}/{length} bytes.");
                total += read;
            }
            return buffer;
        }

        public async Task<byte[]> ReadLoginServerPacketAsync()
        {
            var stream = _loginTestClient.GetStream();
            var buffer = new byte[6];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var total = 0;
            while (total < 6)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, 6 - total), cts.Token);
                if (read == 0) throw new IOException($"Connection closed after reading {total}/6 bytes.");
                total += read;
            }
            return buffer;
        }

        public async Task<bool> TryReadLoginServerPacketAsync(TimeSpan timeout)
        {
            var stream = _loginTestClient.GetStream();
            var buffer = new byte[6];
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cts.Token);
                return read > 0;
            }
            catch (OperationCanceledException) { return false; }
        }

        // Disconnects only the MapServer<->CharServer connection (never the injected LoginServer
        // loopback), matching a real MapServer service-connection drop - proves the registry-removal
        // half of the fix independently of the LoginServer socket's own lifetime.
        public void DisconnectSessionOnly()
        {
            _cts.Cancel();
            try { _runTask.GetAwaiter().GetResult(); } catch { /* Expected on cancel/close. */ }
            _session.Dispose();
            _mapServerSide.Dispose();
            _mapTestClient.Dispose();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _runTask.GetAwaiter().GetResult(); } catch { /* expected on cancel/close */ }
            _session.Dispose();
            _mapServerSide.Dispose();
            _mapTestClient.Dispose();
            _mapListener.Stop();
            _loginServerSideOfConnector.Dispose();
            _loginTestClient.Dispose();
            _loginListener.Stop();
        }
    }
}
