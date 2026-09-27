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
/// Security regression coverage: an arbitrary TCP client that has not completed
/// the LcServiceHello -&gt; LcServiceAuthChallenge -&gt; LcServiceAuthProof HMAC-SHA256
/// handshake (see ai/login-server.md, "Inter-server service authentication")
/// must not be able to execute privileged inter-server operations -
/// account-data requests, bans, state changes, registry updates, or player
/// auth requests. Only LcServiceHello/LcServiceAuthProof themselves (the
/// handshake) are accepted before authentication.
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
    private const string ServiceToken = "test-only-service-token-0123456789abcdef";
    private const string ServiceId = "TestCharServer";

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

        await fixture.CompleteServiceHandshakeAsync(expectSuccess: false, proofToken: "a-completely-wrong-token");

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
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb(), configuredToken: null);

        await fixture.CompleteServiceHandshakeAsync(expectSuccess: false, proofToken: ServiceToken);

        var banPacket = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(banPacket.AsSpan(2, 4), 2000001);
        BinaryPrimitives.WriteInt32LittleEndian(banPacket.AsSpan(6, 4), 3600);
        await fixture.InvokeHandlePacketAsync(LcBanAccount, banPacket);

        var receivedAnything = await fixture.TryReadAnyByteAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(receivedAnything, "A connection must fail closed when no ServiceToken is configured at all.");
    }

    private sealed class ClientSessionFixture : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _testClient;
        private readonly TcpClient _serverSide;
        private readonly MethodInfo _handlePacketMethod;

        public ClientSession Session { get; }

        private ClientSessionFixture(ClientSession session, TcpListener listener, TcpClient testClient, TcpClient serverSide)
        {
            Session = session;
            _listener = listener;
            _testClient = testClient;
            _serverSide = serverSide;
            _handlePacketMethod = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        }

        public static ClientSessionFixture Create(Func<AthenaIdentityDbContext?> identityDbFactory, string? configuredToken = ServiceToken)
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
            var secrets = new SecretConfig { CharServerServiceToken = configuredToken ?? string.Empty };
            var serviceAuth = new ServiceAuthenticationService(new CharServerServiceTokenProvider(secrets));
            var session = new Athena.Net.LoginServer.Net.ClientSession(
                serverSide,
                configStore,
                messageStore,
                () => null,
                identityDbFactory,
                new Athena.Net.LoginServer.Net.CharServerRegistry(),
                new Athena.Net.LoginServer.Net.LoginState(),
                new SubnetConfig(),
                new UnavailablePlayerAuthenticationService(),
                serviceAuth);

            return new ClientSessionFixture(session, listener, testClient, serverSide);
        }

        public async Task InvokeHandlePacketAsync(short packetType, byte[] packet)
        {
            await (Task)_handlePacketMethod.Invoke(Session, new object[] { packetType, packet, CancellationToken.None })!;
        }

        /// <summary>
        /// Drives the full LcServiceHello -&gt; LcServiceAuthChallenge -&gt;
        /// LcServiceAuthProof -&gt; LcServiceAuthResult handshake against the
        /// session under test, computing the HMAC-SHA256 proof with
        /// <paramref name="proofToken"/> (defaults to the correctly configured
        /// <see cref="ServiceToken"/>, but a test can pass a different value to
        /// exercise the wrong-token failure path).
        /// </summary>
        public async Task CompleteServiceHandshakeAsync(bool expectSuccess, string proofToken = ServiceToken)
        {
            var hello = BuildServiceHelloPacket(ServiceId);
            await InvokeHandlePacketAsync(LcServiceHello, hello);

            var challengePacket = await ReadExactAsync(2 + ServiceNonceLength);
            Assert.Equal(LcServiceAuthChallenge, BinaryPrimitives.ReadInt16LittleEndian(challengePacket.AsSpan(0, 2)));
            var nonce = challengePacket.AsSpan(2, ServiceNonceLength).ToArray();

            var proof = ServiceAuthProofCalculator.ComputeProof(Encoding.UTF8.GetBytes(proofToken), ServiceId, nonce);
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
