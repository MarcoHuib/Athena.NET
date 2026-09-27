using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Net;
using Athena.Net.LoginServer.Tests.TestSupport;

namespace Athena.Net.LoginServer.Tests.Net;

/// <summary>
/// Security regression coverage for the CharServer HMAC-SHA256 service
/// authentication handshake at the wire/ClientSession level (see
/// ai/login-server.md, "Inter-server service authentication"): the
/// protected-packet gate, the handshake connection state machine
/// (Unauthenticated -&gt; ChallengeIssued -&gt; Authenticated, with any invalid
/// transition failing closed), the real wall-clock handshake timeout, and
/// CharServerRegistry registration hygiene. Proof-integrity/tamper coverage
/// for the v2 HMAC message format itself lives in
/// ServiceAuthenticationServiceTests.cs (Application layer) - the tests here
/// focus on how ClientSession wires that service into the actual packet
/// flow and TCP connection lifecycle.
/// </summary>
public sealed class ClientSessionServiceAuthGateTests : IDisposable
{
    private const short LcServiceHello = 0x2750;
    private const short LcServiceAuthChallenge = 0x2751;
    private const short LcServiceAuthProof = 0x2752;
    private const short LcServiceAuthResult = 0x2753;
    private const short LcBanAccount = 0x2725;
    private const short LcAccountDataRequest = 0x2716;
    private const short LcAuthRequest = 0x2712;
    private const int NameLength = 24;
    private const int ServerNameLength = 20;
    private const int ServiceNonceLength = 32;
    private const int ServiceProofLength = 32;
    private const string ServiceId = "TestCharServer";

    private static readonly string ServiceToken = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly string WrongServiceToken = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public ClientSessionServiceAuthGateTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AthenaIdentityDbContext>(options => options.UseSqlite(_connection));
        services.AddIdentityCore<AthenaIdentityUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 6;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
        })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AthenaIdentityDbContext>();
        services.AddSingleton(new LoginConfigStore(new LoginConfig()));
        services.AddScoped<IRagnarokAccountIdAllocator, SqliteMaxPlusOneRagnarokAccountIdAllocator>();
        services.AddScoped<IPlayerAccountProvisioningService, PlayerAccountProvisioningService>();

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private async Task<ProvisionPlayerAccountResult> ProvisionPlayerAsync(string userName)
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var result = await provisioning.ProvisionAsync(userName, $"{userName}@example.com", "password1", 'M', CancellationToken.None);
        Assert.True(result.Success, result.ErrorMessage);
        return result;
    }

    private AthenaIdentityDbContext CreateIdentityDb()
    {
        var options = new DbContextOptionsBuilder<AthenaIdentityDbContext>().UseSqlite(_connection).Options;
        return new AthenaIdentityDbContext(options);
    }

    // ------------------------------------------------------------------
    // Protected-packet gate
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnauthenticatedSocket_LcBanAccount_IsDropped_AndDoesNotModifyGameAccount()
    {
        var provisioned = await ProvisionPlayerAsync("banvictim");
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        var packet = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), provisioned.RagnarokAccountId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6, 4), 3600);

        await fixture.InvokeHandlePacketAsync(LcBanAccount, packet);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        var account = await db.GameAccounts.AsNoTracking().SingleAsync(a => a.Id == provisioned.GameAccountId);
        Assert.Equal(0u, account.UnbanTime);
    }

    [Fact]
    public async Task UnauthenticatedSocket_LcAccountDataRequest_ReceivesNoResponse()
    {
        var provisioned = await ProvisionPlayerAsync("dataleaktest");
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        var packet = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), provisioned.RagnarokAccountId);

        await fixture.InvokeHandlePacketAsync(LcAccountDataRequest, packet);

        // No bytes should ever arrive: the packet must be dropped before any
        // handler runs, not merely fail to find data.
        var receivedAnything = await fixture.TryReadAnyByteAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(receivedAnything);
    }

    [Fact]
    public async Task UnauthenticatedSocket_LcAuthRequest_IsDropped()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        var packet = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 12345);

        await fixture.InvokeHandlePacketAsync(LcAuthRequest, packet);

        var receivedAnything = await fixture.TryReadAnyByteAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(receivedAnything, "An unauthenticated socket must never receive an LcAuthResponse.");
    }

    [Fact]
    public async Task AfterSuccessfulServiceLogin_LcBanAccount_Succeeds()
    {
        var provisioned = await ProvisionPlayerAsync("realbanflow");
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        await fixture.CompleteServiceHandshakeAsync(expectSuccess: true);

        var banPacket = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(banPacket.AsSpan(2, 4), provisioned.RagnarokAccountId);
        BinaryPrimitives.WriteInt32LittleEndian(banPacket.AsSpan(6, 4), 3600);
        await fixture.InvokeHandlePacketAsync(LcBanAccount, banPacket);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        var account = await db.GameAccounts.AsNoTracking().SingleAsync(a => a.Id == provisioned.GameAccountId);
        Assert.True(account.UnbanTime > 0, "An authenticated CharServer must still be able to ban an account.");
    }

    /// <summary>
    /// Core regression for the "IsAuthenticated must only become true after a
    /// fully successful HMAC-SHA256 proof verification" security invariant: a
    /// proof computed from the wrong token must fail the handshake and leave
    /// the connection unable to run a protected Lc* packet afterwards, on the
    /// SAME session/socket.
    /// </summary>
    [Fact]
    public async Task ServiceHandshake_WrongToken_FailsAndLeavesConnectionUnauthenticated()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        await fixture.CompleteServiceHandshakeAsync(expectSuccess: false, proofTokenBase64: WrongServiceToken);

        var banPacket = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(banPacket.AsSpan(2, 4), 2000001);
        BinaryPrimitives.WriteInt32LittleEndian(banPacket.AsSpan(6, 4), 3600);
        await fixture.InvokeHandlePacketAsync(LcBanAccount, banPacket);

        var receivedAnything = await fixture.TryReadAnyByteAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(receivedAnything, "A connection whose service handshake failed must not be treated as authenticated.");
    }

    [Fact]
    public async Task ServiceHandshake_NoTokenConfigured_FailsClosed()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb(), configuredTokenBase64: string.Empty);

        await fixture.CompleteServiceHandshakeAsync(expectSuccess: false, proofTokenBase64: ServiceToken);

        var banPacket = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(banPacket.AsSpan(2, 4), 2000001);
        BinaryPrimitives.WriteInt32LittleEndian(banPacket.AsSpan(6, 4), 3600);
        await fixture.InvokeHandlePacketAsync(LcBanAccount, banPacket);

        var receivedAnything = await fixture.TryReadAnyByteAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(receivedAnything, "A connection must fail closed when no ServiceToken is configured at all.");
    }

    /// <summary>
    /// Wiring check that ClientSession actually feeds the full ServiceHello
    /// payload into the proof (exhaustive per-field tamper coverage is in
    /// ServiceAuthenticationServiceTests.cs): a proof computed for a
    /// different advertised IP than the one in the hello packet must fail
    /// even though the ServiceId is unchanged.
    /// </summary>
    [Fact]
    public async Task ServiceHandshake_ProofBoundToDifferentIp_Fails()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        await fixture.CompleteServiceHandshakeAsync(
            expectSuccess: false,
            proofTokenBase64: ServiceToken,
            proofIpOverride: IPAddress.Parse("203.0.113.7"));

        Assert.Empty(fixture.CharServers.Servers);
    }

    // ------------------------------------------------------------------
    // Handshake connection state machine
    // ------------------------------------------------------------------

    [Fact]
    public async Task SecondHello_WhileChallengePending_IsRejected_AndClosesConnection()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        await fixture.InvokeHelloAsync();
        var firstChallenge = await fixture.ReadExactAsync(2 + ServiceNonceLength);
        Assert.Equal(LcServiceAuthChallenge, BinaryPrimitives.ReadInt16LittleEndian(firstChallenge.AsSpan(0, 2)));

        await fixture.InvokeHelloAsync(serviceId: "AnotherServer");

        var response = await fixture.ReadExactAsync(3);
        Assert.Equal(LcServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.NotEqual((byte)0, response[2]);
        Assert.Empty(fixture.CharServers.Servers);
    }

    [Fact]
    public async Task Hello_AfterAuthenticated_IsRejected_AndClosesConnection()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());
        await fixture.CompleteServiceHandshakeAsync(expectSuccess: true);
        Assert.Single(fixture.CharServers.Servers);

        await fixture.InvokeHelloAsync();

        var response = await fixture.ReadExactAsync(3);
        Assert.Equal(LcServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.NotEqual((byte)0, response[2]);

        // The original registration must survive an unrelated rejected hello.
        Assert.Single(fixture.CharServers.Servers);
    }

    [Fact]
    public async Task Proof_AfterAuthenticated_IsRejected_AndDoesNotCreateSecondRegistration()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());
        await fixture.CompleteServiceHandshakeAsync(expectSuccess: true);
        Assert.Single(fixture.CharServers.Servers);

        var replayProof = new byte[2 + ServiceProofLength];
        RandomBytes(replayProof.AsSpan(2));
        await fixture.InvokeHandlePacketAsync(LcServiceAuthProof, replayProof);

        var response = await fixture.ReadExactAsync(3);
        Assert.Equal(LcServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.NotEqual((byte)0, response[2]);
        Assert.Single(fixture.CharServers.Servers);
    }

    [Fact]
    public async Task Proof_WithoutOutstandingChallenge_IsRejected_AndClosesConnection()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        var proofPacket = new byte[2 + ServiceProofLength];
        RandomBytes(proofPacket.AsSpan(2));
        await fixture.InvokeHandlePacketAsync(LcServiceAuthProof, proofPacket);

        var response = await fixture.ReadExactAsync(3);
        Assert.Equal(LcServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.NotEqual((byte)0, response[2]);
        Assert.Empty(fixture.CharServers.Servers);
    }

    // ------------------------------------------------------------------
    // CharServerRegistry registration hygiene
    // ------------------------------------------------------------------

    [Fact]
    public async Task SuccessfulHandshake_RegistersExactlyOneCharServer()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        await fixture.CompleteServiceHandshakeAsync(expectSuccess: true);

        Assert.Single(fixture.CharServers.Servers);
    }

    [Fact]
    public void Disconnect_AfterSuccessfulAuth_UnregistersCleanly_NoStaleEntry()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());
        fixture.CompleteServiceHandshakeAsync(expectSuccess: true).GetAwaiter().GetResult();
        Assert.Single(fixture.CharServers.Servers);

        fixture.Session.Dispose();

        Assert.Empty(fixture.CharServers.Servers);
    }

    // ------------------------------------------------------------------
    // Real wall-clock handshake timeout
    // ------------------------------------------------------------------

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task ChallengeIssued_NoProofBeforeTimeout_ConnectionCloses_NoRegistration()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb(), serviceAuthTimeout: TestTimeout);
        using var cts = new CancellationTokenSource();
        var runTask = fixture.Session.RunAsync(cts.Token);

        await fixture.SendHelloAsync();
        var challenge = await fixture.ReadExactAsync(2 + ServiceNonceLength);
        Assert.Equal(LcServiceAuthChallenge, BinaryPrimitives.ReadInt16LittleEndian(challenge.AsSpan(0, 2)));

        // Deliberately send nothing else.
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(runTask, completed);
        await runTask;

        Assert.False(fixture.ServiceAuth.IsAuthenticated);
        Assert.Empty(fixture.CharServers.Servers);

        var receivedAnything = await fixture.TryReadAnyByteAsync(TimeSpan.FromMilliseconds(200));
        Assert.False(receivedAnything, "No further bytes should arrive on a connection closed by the handshake timeout.");
    }

    [Fact]
    public async Task Proof_WithinTimeout_Succeeds()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb(), serviceAuthTimeout: TestTimeout);
        using var cts = new CancellationTokenSource();
        var runTask = fixture.Session.RunAsync(cts.Token);

        await fixture.SendHelloAsync();
        var challenge = await fixture.ReadExactAsync(2 + ServiceNonceLength);
        var nonce = challenge.AsSpan(2, ServiceNonceLength).ToArray();

        var proof = ServiceAuthProofCalculator.ComputeProof(
            Convert.FromBase64String(ServiceToken),
            new ServiceHelloInfo(ServiceId, IPAddress.Loopback, 6121, "TestChar", 0, 0),
            nonce);
        await fixture.SendProofAsync(proof);

        var result = await fixture.ReadExactAsync(3);
        Assert.Equal(LcServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(0, 2)));
        Assert.Equal((byte)0, result[2]);
        Assert.Single(fixture.CharServers.Servers);

        cts.Cancel();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
            // Expected once we cancel the outer token to end the test.
        }
    }

    [Fact]
    public async Task Proof_AfterTimeoutElapsed_IsNeverAuthenticated_NoRegistration()
    {
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb(), serviceAuthTimeout: TestTimeout);
        using var cts = new CancellationTokenSource();
        var runTask = fixture.Session.RunAsync(cts.Token);

        await fixture.SendHelloAsync();
        var challenge = await fixture.ReadExactAsync(2 + ServiceNonceLength);
        var nonce = challenge.AsSpan(2, ServiceNonceLength).ToArray();

        // Let the timeout window fully elapse before attempting the proof.
        await Task.Delay(TestTimeout + TimeSpan.FromMilliseconds(200));

        var proof = ServiceAuthProofCalculator.ComputeProof(
            Convert.FromBase64String(ServiceToken),
            new ServiceHelloInfo(ServiceId, IPAddress.Loopback, 6121, "TestChar", 0, 0),
            nonce);

        // The connection should already be closed by the timeout; sending
        // more bytes must not resurrect it into an authenticated state.
        try
        {
            await fixture.SendProofAsync(proof);
        }
        catch (IOException)
        {
            // Expected if the server already closed its side.
        }
        catch (SocketException)
        {
            // Expected if the server already closed its side.
        }

        await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.False(fixture.ServiceAuth.IsAuthenticated);
        Assert.Empty(fixture.CharServers.Servers);
    }

    private static void RandomBytes(Span<byte> buffer)
    {
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
    }

    private sealed class ClientSessionFixture : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _testClient;
        private readonly TcpClient _serverSide;
        private readonly MethodInfo _handlePacketMethod;

        public ClientSession Session { get; }

        public CharServerRegistry CharServers { get; }

        public ServiceAuthenticationService ServiceAuth { get; }

        private ClientSessionFixture(ClientSession session, CharServerRegistry charServers, ServiceAuthenticationService serviceAuth, TcpListener listener, TcpClient testClient, TcpClient serverSide)
        {
            Session = session;
            CharServers = charServers;
            ServiceAuth = serviceAuth;
            _listener = listener;
            _testClient = testClient;
            _serverSide = serverSide;
            _handlePacketMethod = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        }

        public static ClientSessionFixture Create(
            Func<AthenaIdentityDbContext?> identityDbFactory,
            string? configuredTokenBase64 = null,
            TimeSpan? serviceAuthTimeout = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var testClient = new TcpClient();
            var connectTask = testClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            var serverSide = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();

            var configStore = new LoginConfigStore(new LoginConfig { LogLogin = false });
            var messageStore = new LoginMessageStore(new LoginMessageCatalog(new Dictionary<uint, string>()));
            var secrets = new SecretConfig { CharServerServiceToken = configuredTokenBase64 ?? ServiceToken };
            var serviceAuth = new ServiceAuthenticationService(new CharServerServiceTokenProvider(secrets));
            var charServers = new CharServerRegistry();
            var session = new Athena.Net.LoginServer.Net.ClientSession(
                serverSide,
                configStore,
                messageStore,
                () => null,
                identityDbFactory,
                charServers,
                new Athena.Net.LoginServer.Net.LoginState(),
                new SubnetConfig(),
                new UnavailablePlayerAuthenticationService(),
                serviceAuth,
                identityAccountService: null,
                serviceAuthTimeout: serviceAuthTimeout);

            return new ClientSessionFixture(session, charServers, serviceAuth, listener, testClient, serverSide);
        }

        public async Task InvokeHandlePacketAsync(short packetType, byte[] packet)
        {
            await (Task)_handlePacketMethod.Invoke(Session, new object[] { packetType, packet, CancellationToken.None })!;
        }

        /// <summary>
        /// Dispatches an LcServiceHello directly via <see cref="InvokeHandlePacketAsync"/> -
        /// no real <see cref="ClientSession.RunAsync"/> loop needed, so this is
        /// the right helper for state-machine tests that don't exercise the
        /// wall-clock handshake timeout.
        /// </summary>
        public Task InvokeHelloAsync(string serviceId = ServiceId)
        {
            return InvokeHandlePacketAsync(LcServiceHello, BuildServiceHelloPacket(serviceId));
        }

        /// <summary>
        /// Sends a raw LcServiceHello over the actual socket (as opposed to
        /// <see cref="InvokeHandlePacketAsync"/>'s reflection shortcut) - used
        /// by the timeout tests, which need <see cref="ClientSession.RunAsync"/>
        /// actually running its read loop for the handshake timeout to have
        /// anything to cancel.
        /// </summary>
        public async Task SendHelloAsync(string serviceId = ServiceId)
        {
            var hello = BuildServiceHelloPacket(serviceId);
            var stream = _testClient.GetStream();
            await stream.WriteAsync(hello);
        }

        public async Task SendProofAsync(byte[] proof)
        {
            var packet = new byte[2 + ServiceProofLength];
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), LcServiceAuthProof);
            proof.CopyTo(packet, 2);
            var stream = _testClient.GetStream();
            await stream.WriteAsync(packet);
        }

        /// <summary>
        /// Drives the full LcServiceHello -&gt; LcServiceAuthChallenge -&gt;
        /// LcServiceAuthProof -&gt; LcServiceAuthResult handshake against the
        /// session under test via the reflection-based dispatch (no real
        /// RunAsync loop, so no handshake timeout applies here), computing
        /// the HMAC-SHA256 proof with <paramref name="proofTokenBase64"/>
        /// (defaults to the correctly configured <see cref="ServiceToken"/>,
        /// but a test can pass a different value to exercise the wrong-token
        /// failure path) and optionally binding the proof to a different IP
        /// than the one actually sent in the hello, to exercise the
        /// full-payload-binding wiring.
        /// </summary>
        public async Task CompleteServiceHandshakeAsync(
            bool expectSuccess,
            string? proofTokenBase64 = null,
            IPAddress? proofIpOverride = null)
        {
            var hello = BuildServiceHelloPacket(ServiceId);
            await InvokeHandlePacketAsync(LcServiceHello, hello);

            var challengePacket = await ReadExactAsync(2 + ServiceNonceLength);
            Assert.Equal(LcServiceAuthChallenge, BinaryPrimitives.ReadInt16LittleEndian(challengePacket.AsSpan(0, 2)));
            var nonce = challengePacket.AsSpan(2, ServiceNonceLength).ToArray();

            var helloInfo = new ServiceHelloInfo(ServiceId, proofIpOverride ?? IPAddress.Loopback, 6121, "TestChar", 0, 0);
            var proof = ServiceAuthProofCalculator.ComputeProof(Convert.FromBase64String(proofTokenBase64 ?? ServiceToken), helloInfo, nonce);
            var proofPacket = new byte[2 + ServiceProofLength];
            proof.CopyTo(proofPacket, 2);
            await InvokeHandlePacketAsync(LcServiceAuthProof, proofPacket);

            var resultPacket = await ReadExactAsync(3);
            Assert.Equal(LcServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(resultPacket.AsSpan(0, 2)));
            if (expectSuccess)
            {
                Assert.Equal((byte)0, resultPacket[2]);
            }
            else
            {
                Assert.NotEqual((byte)0, resultPacket[2]);
            }
        }

        private static byte[] BuildServiceHelloPacket(string serviceId)
        {
            var packet = new byte[2 + NameLength + 4 + 2 + ServerNameLength + 2 + 2];
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), LcServiceHello);
            WriteFixedAscii(packet, 2, NameLength, serviceId);
            IPAddress.Loopback.GetAddressBytes().CopyTo(packet, 2 + NameLength);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2 + NameLength + 4, 2), 6121);
            WriteFixedAscii(packet, 2 + NameLength + 4 + 2, ServerNameLength, "TestChar");
            return packet;
        }

        private static void WriteFixedAscii(byte[] buffer, int offset, int length, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, buffer, offset, Math.Min(length, bytes.Length));
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

        public async Task<bool> TryReadAnyByteAsync(TimeSpan timeout)
        {
            var stream = _testClient.GetStream();
            var buffer = new byte[1];
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cts.Token);
                return read > 0;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            Session.Dispose();
            _serverSide.Dispose();
            _testClient.Dispose();
            _listener.Stop();
        }
    }
}
