using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

/// <summary>
/// Covers the MapServer &lt;-&gt; CharServer HMAC-SHA256 service authentication
/// handshake at the wire/MapServerSession level (see ai/char-server.md,
/// "Inter-server service authentication (MapServer)"): success, every
/// documented failure mode, replay resistance, and that gameplay/persistence
/// packets are rejected before authentication. This handshake and its
/// MapServer ServiceToken are completely independent of the pre-existing
/// CharServer &lt;-&gt; LoginServer handshake (covered separately in
/// ServiceAuthProofCalculatorTests.cs / CharServerServiceTokenProviderTests.cs).
/// Deterministic 32-byte test tokens only - never a real secret.
/// <para>
/// The HMAC proof itself is computed here via
/// <see cref="MapServiceAuthProofCalculatorAccessor"/>, a thin reflection
/// shim over the internal <c>MapServiceAuthProofCalculator</c> class (this
/// test project cannot add an InternalsVisibleTo without touching production
/// code, and the class is intentionally internal - only MapServerSession and
/// this test need it).
/// </para>
/// </summary>
public sealed class MapServerSessionServiceAuthTests : IDisposable
{
    private const int NameLength = 24;
    private const int ServiceNonceLength = 32;
    private const int ServiceProofLength = 32;
    private const string ServiceId = "TestMapServer";

    private static readonly string ServiceToken = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly string WrongServiceToken = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());

    private readonly List<Fixture> _fixtures = new();

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
        {
            fixture.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // SUCCESS
    // ------------------------------------------------------------------

    [Fact]
    public async Task ValidHandshake_AuthenticatesAndRegistersMapServer()
    {
        var fixture = CreateFixture();

        await fixture.CompleteHandshakeAsync(expectSuccess: true);

        Assert.True(fixture.Registry.TryGetAny(out _));
    }

    [Fact]
    public async Task AfterSuccessfulHandshake_NormalPacketFlowWorks()
    {
        var fixture = CreateFixture();
        await fixture.CompleteHandshakeAsync(expectSuccess: true);

        // MapSendMaps: variable-length [opcode.W][length.W][mapname*16...].
        var packet = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.MapSendMaps);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 4);
        await fixture.SendRawAsync(packet);

        await Task.Delay(150);

        // The connection must still be registered/alive after a normal
        // post-auth gameplay/registration packet - it must not have been
        // rejected or the connection closed.
        Assert.True(fixture.Registry.TryGetAny(out _));
    }

    // ------------------------------------------------------------------
    // FAILURE
    // ------------------------------------------------------------------

    [Fact]
    public async Task MissingMapServerToken_FailsClosed()
    {
        var fixture = CreateFixture(configuredTokenBase64: string.Empty);

        await fixture.CompleteHandshakeAsync(expectSuccess: false, proofTokenBase64: ServiceToken);

        Assert.False(fixture.Registry.TryGetAny(out _));
    }

    [Fact]
    public async Task InvalidBase64Token_IsTreatedAsNotConfigured_FailsClosed()
    {
        var fixture = CreateFixture(configuredTokenBase64: "not-valid-base64!!!");

        await fixture.CompleteHandshakeAsync(expectSuccess: false, proofTokenBase64: ServiceToken);

        Assert.False(fixture.Registry.TryGetAny(out _));
    }

    [Fact]
    public async Task TokenShorterThan32Bytes_IsTreatedAsNotConfigured_FailsClosed()
    {
        var shortToken = Convert.ToBase64String(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        var fixture = CreateFixture(configuredTokenBase64: shortToken);

        await fixture.CompleteHandshakeAsync(expectSuccess: false, proofTokenBase64: shortToken);

        Assert.False(fixture.Registry.TryGetAny(out _));
    }

    [Fact]
    public async Task WrongToken_ProofRejected_MapServerNotRegistered()
    {
        var fixture = CreateFixture();

        await fixture.CompleteHandshakeAsync(expectSuccess: false, proofTokenBase64: WrongServiceToken);

        Assert.False(fixture.Registry.TryGetAny(out _));
    }

    [Fact]
    public async Task IncorrectProofBytes_Rejected()
    {
        var fixture = CreateFixture();

        await fixture.SendHelloAsync();
        var challenge = await fixture.ReadExactAsync(2 + ServiceNonceLength);
        Assert.Equal(PacketConstants.MapServiceAuthChallenge, BinaryPrimitives.ReadInt16LittleEndian(challenge.AsSpan(0, 2)));

        var garbageProof = new byte[ServiceProofLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(garbageProof);
        await fixture.SendProofAsync(garbageProof);

        var result = await fixture.ReadExactAsync(3);
        Assert.Equal(PacketConstants.MapServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(0, 2)));
        Assert.NotEqual((byte)0, result[2]);
        Assert.False(fixture.Registry.TryGetAny(out _));
    }

    [Fact]
    public async Task MalformedChallengeNeverIssued_ProofBeforeAnyHello_IsRejected()
    {
        var fixture = CreateFixture();

        var proof = new byte[ServiceProofLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(proof);
        await fixture.SendProofAsync(proof);

        var result = await fixture.ReadExactAsync(3);
        Assert.Equal(PacketConstants.MapServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(0, 2)));
        Assert.NotEqual((byte)0, result[2]);
        Assert.False(fixture.Registry.TryGetAny(out _));
    }

    [Fact]
    public async Task GameplayPacket_BeforeAuthentication_IsNotAccepted()
    {
        var fixture = CreateFixture();

        // MapSendMaps sent before any hello/auth: must not authenticate or
        // register the connection as a side effect.
        var packet = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.MapSendMaps);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 4);
        await fixture.SendRawAsync(packet);

        await Task.Delay(150);
        Assert.False(fixture.Registry.TryGetAny(out _));
    }

    [Fact]
    public async Task ReplayOfSuccessfulProof_DoesNotAuthenticateASecondTime()
    {
        var fixture = CreateFixture();
        var (nonce, hello) = await fixture.SendHelloAndReadChallengeAsync();
        var proof = MapServiceAuthProofCalculatorAccessor.ComputeProof(Convert.FromBase64String(ServiceToken), hello, nonce);
        await fixture.SendProofAsync(proof);

        var firstResult = await fixture.ReadExactAsync(3);
        Assert.Equal((byte)0, firstResult[2]);
        Assert.True(fixture.Registry.TryGetAny(out _));

        // Replay the identical proof bytes again on the same connection.
        await fixture.SendProofAsync(proof);

        var secondResult = await fixture.ReadExactAsync(3);
        Assert.NotEqual((byte)0, secondResult[2]);

        // Still exactly one registration - the replay must not create a second one.
        Assert.Equal(1, fixture.Registry.Count);
    }

    [Fact]
    public async Task SecondAuthAttemptOnAlreadyAuthenticatedConnection_CannotBypassAuthentication()
    {
        var fixture = CreateFixture();
        await fixture.CompleteHandshakeAsync(expectSuccess: true);
        Assert.True(fixture.Registry.TryGetAny(out _));

        // A second MapServiceHello on an already-authenticated connection is
        // rejected and the connection is closed outright (fail closed) -
        // there is no guarantee a graceful MapServiceAuthResult response
        // arrives before the socket closes, so this asserts on the
        // observable security property instead: no further authentication
        // occurred and the original registration is untouched.
        await fixture.SendHelloAsync(serviceId: "AttackerReplay");

        await Task.Delay(150);

        Assert.Equal(1, fixture.Registry.Count);
    }

    [Fact]
    public async Task ChallengeFromDifferentConnection_CannotAuthenticateThisConnection()
    {
        var fixtureA = CreateFixture();
        var (nonceA, helloA) = await fixtureA.SendHelloAndReadChallengeAsync();
        var proofA = MapServiceAuthProofCalculatorAccessor.ComputeProof(Convert.FromBase64String(ServiceToken), helloA, nonceA);

        var fixtureB = CreateFixture();
        await fixtureB.SendHelloAsync();
        await fixtureB.ReadExactAsync(2 + ServiceNonceLength);

        // Send connection A's valid proof to connection B - B never issued this nonce.
        await fixtureB.SendProofAsync(proofA);

        var result = await fixtureB.ReadExactAsync(3);
        Assert.NotEqual((byte)0, result[2]);
        Assert.False(fixtureB.Registry.TryGetAny(out _));
    }

    // ------------------------------------------------------------------
    // CONFIG
    // ------------------------------------------------------------------

    [Fact]
    public void SecretConfig_LoadsMapServerServiceToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "athena-char-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var secretsPath = Path.Combine(tempDir, "secret.json");
        File.WriteAllText(secretsPath, $"{{\"ServiceAuthentication\":{{\"MapServer\":{{\"Token\":\"{ServiceToken}\"}}}}}}");

        var secrets = SecretConfig.Load(secretsPath);

        Assert.Equal(ServiceToken, secrets.MapServerServiceToken);
    }

    [Fact]
    public void SecretConfig_LoadsBothTokensIndependently()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "athena-char-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var secretsPath = Path.Combine(tempDir, "secret.json");
        var charToken = ServiceToken;
        var mapToken = WrongServiceToken;
        File.WriteAllText(
            secretsPath,
            $"{{\"ServiceAuthentication\":{{\"CharServer\":{{\"Token\":\"{charToken}\"}},\"MapServer\":{{\"Token\":\"{mapToken}\"}}}}}}");

        var secrets = SecretConfig.Load(secretsPath);

        Assert.Equal(charToken, secrets.CharServerServiceToken);
        Assert.Equal(mapToken, secrets.MapServerServiceToken);
        Assert.NotEqual(secrets.CharServerServiceToken, secrets.MapServerServiceToken);
    }

    [Fact]
    public void EnvironmentVariable_OverridesConfiguredToken()
    {
        var envVar = MapServerServiceTokenProvider.EnvironmentVariableName;
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, ServiceToken);
            var secrets = new SecretConfig { MapServerServiceToken = WrongServiceToken };

            var provider = new MapServerServiceTokenProvider(secrets);

            Assert.True(provider.IsConfigured);
            Assert.Equal(Convert.FromBase64String(ServiceToken), provider.TokenBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void LegacyMapServerUserIdPassword_NoLongerExistsOnConfigTypes()
    {
        // Structural regression: CharConfig/SecretConfig must not expose a
        // UserId/Password member anywhere related to MapServer<->CharServer auth.
        var charConfigType = typeof(Athena.Net.CharServer.Config.CharConfig);
        Assert.Null(charConfigType.GetProperty("UserId"));
        Assert.Null(charConfigType.GetProperty("Password"));

        var secretConfigType = typeof(SecretConfig);
        Assert.Null(secretConfigType.GetProperty("CharServerUserId"));
        Assert.Null(secretConfigType.GetProperty("CharServerPassword"));
        Assert.NotNull(secretConfigType.GetProperty("MapServerServiceToken"));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private Fixture CreateFixture(string? configuredTokenBase64 = null)
    {
        var fixture = Fixture.Create(configuredTokenBase64 ?? ServiceToken);
        _fixtures.Add(fixture);
        return fixture;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _testClient;
        private readonly TcpClient _serverSide;
        private readonly CancellationTokenSource _cts = new();
        private readonly MapServerSession _session;
        private readonly Task _runTask;

        private Fixture(TcpListener listener, TcpClient testClient, TcpClient serverSide, MapServerSession session, MapServerRegistry registry)
        {
            _listener = listener;
            _testClient = testClient;
            _serverSide = serverSide;
            _session = session;
            Registry = registry;
            _runTask = _session.RunAsync(_cts.Token);
        }

        public MapServerRegistry Registry { get; }

        public static Fixture Create(string configuredTokenBase64)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var testClient = new TcpClient();
            var connectTask = testClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            var serverSide = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();

            var configStore = new CharConfigStore(new Athena.Net.CharServer.Config.CharConfig(), "unused.conf");
            var registry = new MapServerRegistry();
            var authManager = new MapAuthManager();
            var secrets = new SecretConfig { MapServerServiceToken = configuredTokenBase64 };
            var tokenProvider = new MapServerServiceTokenProvider(secrets);
            var serviceAuth = new MapServiceAuthenticationService(tokenProvider);

            var loginConnector = new LoginServerConnector(configStore, new CharServerServiceTokenProvider(new SecretConfig()));
            var session = new MapServerSession(
                sessionId: 1,
                client: serverSide,
                configStore: configStore,
                registry: registry,
                loginConnector: loginConnector,
                authManager: authManager,
                dbFactory: () => null,
                serviceAuth: serviceAuth,
                prefetchedHeader: null);

            return new Fixture(listener, testClient, serverSide, session, registry);
        }

        public async Task SendRawAsync(byte[] packet)
        {
            var stream = _testClient.GetStream();
            await stream.WriteAsync(packet);
        }

        public Task SendHelloAsync(string serviceId = ServiceId)
        {
            return SendRawAsync(BuildHelloPacket(serviceId));
        }

        public async Task<(byte[] Nonce, MapServiceHelloInfo Hello)> SendHelloAndReadChallengeAsync(string serviceId = ServiceId)
        {
            await SendHelloAsync(serviceId);
            var challenge = await ReadExactAsync(2 + ServiceNonceLength);
            Assert.Equal(PacketConstants.MapServiceAuthChallenge, BinaryPrimitives.ReadInt16LittleEndian(challenge.AsSpan(0, 2)));
            var nonce = challenge.AsSpan(2, ServiceNonceLength).ToArray();
            var hello = new MapServiceHelloInfo(serviceId, IPAddress.Loopback, 5121);
            return (nonce, hello);
        }

        public async Task SendProofAsync(byte[] proof)
        {
            var packet = new byte[2 + ServiceProofLength];
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.MapServiceAuthProof);
            proof.CopyTo(packet, 2);
            await SendRawAsync(packet);
        }

        public async Task CompleteHandshakeAsync(bool expectSuccess, string? proofTokenBase64 = null)
        {
            var (nonce, hello) = await SendHelloAndReadChallengeAsync();
            var proof = MapServiceAuthProofCalculatorAccessor.ComputeProof(Convert.FromBase64String(proofTokenBase64 ?? ServiceToken), hello, nonce);
            await SendProofAsync(proof);

            var result = await ReadExactAsync(3);
            Assert.Equal(PacketConstants.MapServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(0, 2)));
            if (expectSuccess)
            {
                Assert.Equal((byte)0, result[2]);
            }
            else
            {
                Assert.NotEqual((byte)0, result[2]);
            }
        }

        private static byte[] BuildHelloPacket(string serviceId)
        {
            var packet = new byte[2 + NameLength + 4 + 2 + 2];
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.MapServiceHello);
            var idBytes = Encoding.ASCII.GetBytes(serviceId);
            Buffer.BlockCopy(idBytes, 0, packet, 2, Math.Min(NameLength, idBytes.Length));
            IPAddress.Loopback.GetAddressBytes().CopyTo(packet, 2 + NameLength);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2 + NameLength + 4, 2), 5121);
            return packet;
        }

        public async Task<byte[]> ReadExactAsync(int length)
        {
            var stream = _testClient.GetStream();
            var buffer = new byte[length];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var total = 0;
            while (total < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, length - total), cts.Token);
                if (read == 0)
                {
                    throw new IOException($"Connection closed after reading {total}/{length} bytes.");
                }
                total += read;
            }
            return buffer;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _runTask.GetAwaiter().GetResult(); } catch { /* expected on cancel/close */ }
            _session.Dispose();
            _serverSide.Dispose();
            _testClient.Dispose();
            _listener.Stop();
        }
    }
}

/// <summary>
/// Test-only reimplementation of the internal <c>MapServiceAuthProofCalculator</c>
/// wire format (see that class's doc comment in
/// src/CharServer/Net/MapServiceAuthProofCalculator.cs, byte-for-byte
/// identical to src/MapServer/Net/ServiceAuthProofCalculator.cs), so tests
/// can compute the exact same HMAC-SHA256 proof the production MapServer
/// client would without touching that class's intentional <c>internal</c>
/// visibility (a <see cref="System.Reflection.MethodInfo.Invoke"/> call
/// cannot pass a <c>ReadOnlySpan&lt;byte&gt;</c> argument, so reflection is
/// not an option here either).
/// </summary>
internal static class MapServiceAuthProofCalculatorAccessor
{
    private const string Context = "Athena.NET/MapServer/Auth/v1";
    private const byte FieldSeparator = 0x1F;

    public static byte[] ComputeProof(byte[] token, MapServiceHelloInfo hello, byte[] nonce)
    {
        var contextBytes = Encoding.UTF8.GetBytes(Context);
        var serviceIdBytes = Encoding.UTF8.GetBytes(hello.ServiceId);
        var ipBytes = hello.Ip.MapToIPv4().GetAddressBytes();

        var length = contextBytes.Length + 1
            + 1 + serviceIdBytes.Length + 1
            + ipBytes.Length + 2
            + nonce.Length;

        var message = new byte[length];
        var offset = 0;

        contextBytes.CopyTo(message, offset);
        offset += contextBytes.Length;
        message[offset++] = FieldSeparator;

        message[offset++] = (byte)serviceIdBytes.Length;
        serviceIdBytes.CopyTo(message, offset);
        offset += serviceIdBytes.Length;
        message[offset++] = FieldSeparator;

        ipBytes.CopyTo(message, offset);
        offset += ipBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), hello.Port);
        offset += 2;

        nonce.CopyTo(message, offset);

        using var hmac = new System.Security.Cryptography.HMACSHA256(token);
        return hmac.ComputeHash(message);
    }
}
