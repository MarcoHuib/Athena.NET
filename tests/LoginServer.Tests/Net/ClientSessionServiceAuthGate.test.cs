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
using Athena.Net.LoginServer.Db.Entities;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Tests.Net;

/// <summary>
/// Security regression coverage: an arbitrary TCP client that has not proven it
/// is an authenticated CharServer (via LcCharServerLogin) must not be able to
/// execute privileged inter-server operations - account-data requests, bans,
/// state changes, registry updates, or player auth requests. Only
/// LcCharServerLogin itself is accepted before authentication.
/// </summary>
public sealed class ClientSessionServiceAuthGateTests : IDisposable
{
    private const short LcCharServerLogin = 0x2710;
    private const short LcCharServerLoginAck = 0x2711;
    private const short LcBanAccount = 0x2725;
    private const short LcAccountDataRequest = 0x2716;
    private const short LcAccountDataResponse = 0x2717;
    private const short LcAuthRequest = 0x2712;
    private const short LcAuthResponse = 0x2713;
    private const int NameLength = 24;

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

    private static LoginDbContext CreateLoginDb(string name)
    {
        var options = new DbContextOptionsBuilder<LoginDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new LoginDbContext(options);
    }

    [Fact]
    public async Task UnauthenticatedSocket_LcBanAccount_IsDropped_AndDoesNotModifyGameAccount()
    {
        var provisioned = await ProvisionPlayerAsync("banvictim");
        using var fixture = ClientSessionFixture.Create(() => null, () => CreateIdentityDb());

        var packet = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), provisioned.RagnarokAccountId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6, 4), 3600);

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { LcBanAccount, packet, CancellationToken.None })!;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        var account = await db.GameAccounts.AsNoTracking().SingleAsync(a => a.Id == provisioned.GameAccountId);
        Assert.Equal(0u, account.UnbanTime);
    }

    [Fact]
    public async Task UnauthenticatedSocket_LcAccountDataRequest_ReceivesNoResponse()
    {
        var provisioned = await ProvisionPlayerAsync("dataleaktest");
        using var fixture = ClientSessionFixture.Create(() => null, () => CreateIdentityDb());

        var packet = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), provisioned.RagnarokAccountId);

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { LcAccountDataRequest, packet, CancellationToken.None })!;

        // No bytes should ever arrive: the packet must be dropped before any
        // handler runs, not merely fail to find data.
        var receivedAnything = await fixture.TryReadAnyByteAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(receivedAnything);
    }

    [Fact]
    public async Task UnauthenticatedSocket_LcAuthRequest_IsDropped()
    {
        using var fixture = ClientSessionFixture.Create(() => null, () => CreateIdentityDb());

        var packet = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 12345);

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { LcAuthRequest, packet, CancellationToken.None })!;

        var receivedAnything = await fixture.TryReadAnyByteAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(receivedAnything, "An unauthenticated socket must never receive an LcAuthResponse.");
    }

    [Fact]
    public async Task AfterSuccessfulServiceLogin_LcBanAccount_Succeeds()
    {
        var provisioned = await ProvisionPlayerAsync("realbanflow");

        var loginDbName = Guid.NewGuid().ToString();
        await using (var seedDb = CreateLoginDb(loginDbName))
        {
            seedDb.Accounts.Add(new LoginAccount { AccountId = 1, UserId = "charserver", UserPass = "service-secret", Sex = "S" });
            await seedDb.SaveChangesAsync();
        }

        using var fixture = ClientSessionFixture.Create(() => CreateLoginDb(loginDbName), () => CreateIdentityDb());

        var loginPacket = BuildCharServerLoginPacket("charserver", "service-secret");
        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { LcCharServerLogin, loginPacket, CancellationToken.None })!;

        var loginAck = await fixture.ReadExactAsync(3);
        Assert.Equal(LcCharServerLoginAck, BinaryPrimitives.ReadInt16LittleEndian(loginAck.AsSpan(0, 2)));
        Assert.Equal((byte)0, loginAck[2]); // service login succeeded

        var banPacket = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(banPacket.AsSpan(2, 4), provisioned.RagnarokAccountId);
        BinaryPrimitives.WriteInt32LittleEndian(banPacket.AsSpan(6, 4), 3600);
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { LcBanAccount, banPacket, CancellationToken.None })!;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        var account = await db.GameAccounts.AsNoTracking().SingleAsync(a => a.Id == provisioned.GameAccountId);
        Assert.True(account.UnbanTime > 0, "An authenticated CharServer must still be able to ban an account.");
    }

    private static byte[] BuildCharServerLoginPacket(string user, string pass)
    {
        var packet = new byte[86];
        WriteFixedAscii(packet, 2, NameLength, user);
        WriteFixedAscii(packet, 26, NameLength, pass);
        IPAddress.Loopback.GetAddressBytes().CopyTo(packet, 54);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(58, 2), 6121);
        WriteFixedAscii(packet, 60, 20, "TestChar");
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(82, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(84, 2), 0);
        return packet;
    }

    private static void WriteFixedAscii(byte[] buffer, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, Math.Min(length, bytes.Length));
    }

    private sealed class ClientSessionFixture : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _testClient;
        private readonly TcpClient _serverSide;

        public ClientSession Session { get; }

        private ClientSessionFixture(ClientSession session, TcpListener listener, TcpClient testClient, TcpClient serverSide)
        {
            Session = session;
            _listener = listener;
            _testClient = testClient;
            _serverSide = serverSide;
        }

        public static ClientSessionFixture Create(Func<LoginDbContext?> dbFactory, Func<AthenaIdentityDbContext?> identityDbFactory)
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
            var session = new Athena.Net.LoginServer.Net.ClientSession(
                serverSide,
                configStore,
                messageStore,
                dbFactory,
                identityDbFactory,
                new Athena.Net.LoginServer.Net.CharServerRegistry(),
                new Athena.Net.LoginServer.Net.LoginState(),
                new SubnetConfig(),
                new UnavailablePlayerAuthenticationService(),
                new ServiceAuthenticationService());

            return new ClientSessionFixture(session, listener, testClient, serverSide);
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
