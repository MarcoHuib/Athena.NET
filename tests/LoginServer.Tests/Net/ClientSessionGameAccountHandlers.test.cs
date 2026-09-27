using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
/// Regression coverage for the CharServer inter-server Lc* handlers that were
/// repointed from the legacy LoginAccount table to AthenaGameAccount as part of
/// the Identity migration - bans, VIP, account state, and account-data
/// responses must keep working against the new schema, keyed by
/// RagnarokAccountId exactly as before.
/// </summary>
public sealed class ClientSessionGameAccountHandlersTests : IDisposable
{
    private const short LcAccountDataResponse = 0x2717;

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public ClientSessionGameAccountHandlersTests()
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

    private async Task<ProvisionPlayerAccountResult> ProvisionAsync(string userName)
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var result = await provisioning.ProvisionAsync(userName, $"{userName}@example.com", "password1", 'M', CancellationToken.None);
        Assert.True(result.Success, result.ErrorMessage);
        return result;
    }

    private async Task<AthenaGameAccount> GetGameAccountAsync(Guid gameAccountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        return await db.GameAccounts.AsNoTracking().SingleAsync(a => a.Id == gameAccountId);
    }

    [Fact]
    public async Task HandleBanAccountAsync_UpdatesAthenaGameAccount_ByRagnarokAccountId()
    {
        var provisioned = await ProvisionAsync("banhandler");
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        var packet = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), provisioned.RagnarokAccountId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6, 4), 3600); // ban for 1 hour

        var method = typeof(ClientSession).GetMethod("HandleBanAccountAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)method!.Invoke(fixture.Session, new object[] { packet, CancellationToken.None })!;

        var account = await GetGameAccountAsync(provisioned.GameAccountId);
        Assert.True(account.UnbanTime > 0);
    }

    [Fact]
    public async Task HandleUpdateAccountStateAsync_UpdatesAthenaGameAccount_ByRagnarokAccountId()
    {
        var provisioned = await ProvisionAsync("statehandler");
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        var packet = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), provisioned.RagnarokAccountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6, 4), 5);

        var method = typeof(ClientSession).GetMethod("HandleUpdateAccountStateAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)method!.Invoke(fixture.Session, new object[] { packet, CancellationToken.None })!;

        var account = await GetGameAccountAsync(provisioned.GameAccountId);
        Assert.Equal(5u, account.State);
    }

    [Fact]
    public async Task HandleUnbanAccountAsync_ClearsUnbanTime_OnAthenaGameAccount()
    {
        var provisioned = await ProvisionAsync("unbanhandler");
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
            var account = await db.GameAccounts.SingleAsync(a => a.Id == provisioned.GameAccountId);
            account.UnbanTime = 4000000000;
            await db.SaveChangesAsync();
        }

        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());
        var packet = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), provisioned.RagnarokAccountId);

        var method = typeof(ClientSession).GetMethod("HandleUnbanAccountAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)method!.Invoke(fixture.Session, new object[] { packet, CancellationToken.None })!;

        var updated = await GetGameAccountAsync(provisioned.GameAccountId);
        Assert.Equal(0u, updated.UnbanTime);
    }

    [Fact]
    public async Task HandleChangeSexAsync_TogglesSex_OnAthenaGameAccount()
    {
        var provisioned = await ProvisionAsync("sexhandler");
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        var packet = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), provisioned.RagnarokAccountId);

        var method = typeof(ClientSession).GetMethod("HandleChangeSexAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)method!.Invoke(fixture.Session, new object[] { packet, CancellationToken.None })!;

        var account = await GetGameAccountAsync(provisioned.GameAccountId);
        Assert.Equal("F", account.Sex); // provisioned as 'M'
    }

    [Fact]
    public async Task HandleAccountDataRequestAsync_RespondsWithGameAccountAndIdentityEmail()
    {
        var provisioned = await ProvisionAsync("datahandler");
        using var fixture = ClientSessionFixture.Create(() => CreateIdentityDb());

        var packet = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), provisioned.RagnarokAccountId);

        var method = typeof(ClientSession).GetMethod("HandleAccountDataRequestAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)method!.Invoke(fixture.Session, new object[] { packet, CancellationToken.None })!;

        var response = await fixture.ReadExactAsync(75);
        Assert.Equal(LcAccountDataResponse, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(provisioned.RagnarokAccountId, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4)));

        var emailBytes = response.AsSpan(6, 40);
        var nullIndex = emailBytes.IndexOf((byte)0);
        var email = System.Text.Encoding.ASCII.GetString(nullIndex >= 0 ? emailBytes[..nullIndex] : emailBytes);
        Assert.Equal("datahandler@example.com", email);
    }

    private AthenaIdentityDbContext CreateIdentityDb()
    {
        // Mirrors production's short-lived-per-call AthenaIdentityDbContext
        // factory, sharing the same open Sqlite connection/schema the
        // provisioning setup above used.
        var options = new DbContextOptionsBuilder<AthenaIdentityDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AthenaIdentityDbContext(options);
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

        public static ClientSessionFixture Create(Func<AthenaIdentityDbContext?> identityDbFactory)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var testClient = new TcpClient();
            var connectTask = testClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            var serverSide = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();

            var configStore = new LoginConfigStore(new LoginConfig());
            var messageStore = new LoginMessageStore(new LoginMessageCatalog(new Dictionary<uint, string>()));
            var session = new ClientSession(
                serverSide,
                configStore,
                messageStore,
                () => null,
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

        public void Dispose()
        {
            Session.Dispose();
            _serverSide.Dispose();
            _testClient.Dispose();
            _listener.Stop();
        }
    }
}
