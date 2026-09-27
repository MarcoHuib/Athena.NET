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
using Athena.Net.LoginServer.Tests.TestSupport;

namespace Athena.Net.LoginServer.Tests.Net;

/// <summary>
/// End-to-end regression coverage for the Login -&gt; Char handoff: a real player
/// login through ASP.NET Core Identity + AthenaGameAccount. This is the "golden
/// path" definition-of-done gate for the Identity migration. CharServer's own
/// HMAC-SHA256 service authentication handshake against LoginServer is covered
/// separately in ClientSessionServiceAuthGate.test.cs; the WebAuthToken test
/// below drives it only as a prerequisite for the offline-notification flow.
/// </summary>
public sealed class ClientSessionLoginCharFlowTests
{
    private const short CaLogin = 0x64;
    private const short AcAcceptLogin = 0x0A4D;
    private const short AcRefuseLogin = 0x083E;
    private const short LcServiceHello = 0x2750;
    private const short LcServiceAuthChallenge = 0x2751;
    private const short LcServiceAuthProof = 0x2752;
    private const short LcServiceAuthResult = 0x2753;
    private const int NameLength = 24;
    private const int ServerNameLength = 20;
    private const int ServiceNonceLength = 32;
    private const int ServiceProofLength = 32;
    private static readonly string ServiceToken = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private const string ServiceId = "TestCharServer";

    [Fact]
    public async Task StockLogin_ValidPlayerCredentials_ReturnsAcAcceptLogin()
    {
        // Arrange: a real Identity user + AthenaGameAccount, provisioned exactly as
        // production account creation would.
        using var identity = new IdentityTestFixture();
        var provisioned = await identity.ProvisionAsync("playerone", "correct-password", 'M');

        using var fixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);
        // A player login only succeeds when at least one CharServer is registered
        // (otherwise LoginServer sends SC_NOTIFY_BAN instead of AC_ACCEPT_LOGIN).
        fixture.RegisterCharServer(1, "Chaos");

        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 18);
        WriteFixedAscii(packet, 6, NameLength, "playerone");
        WriteFixedAscii(packet, 30, NameLength, "correct-password");
        packet[54] = 0;

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handlePacket);

        // Act
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(96);

        // Assert: successful login always returns AC_ACCEPT_LOGIN (0x0A4D) with the
        // game account's RagnarokAccountId (see ai/iro-2026-wire.md).
        Assert.Equal(AcAcceptLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(provisioned.RagnarokAccountId, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8, 4)));
        Assert.Equal((byte)1, response[46]); // sex M -> 1
    }

    [Fact]
    public async Task StockLogin_WrongPassword_ReturnsAcRefuseLogin()
    {
        // Arrange
        using var identity = new IdentityTestFixture();
        await identity.ProvisionAsync("playertwo", "the-real-password", 'M');

        using var fixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);

        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 18);
        WriteFixedAscii(packet, 6, NameLength, "playertwo");
        WriteFixedAscii(packet, 30, NameLength, "totally-wrong");
        packet[54] = 0;

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        // Act
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(26);

        // Assert: AC_REFUSE_LOGIN (0x083E) with error code 1 (incorrect password).
        Assert.Equal(AcRefuseLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4)));
    }

    [Fact]
    public async Task StockLogin_UnknownUsername_ReturnsAcRefuseLoginWithCode0()
    {
        using var identity = new IdentityTestFixture();
        using var fixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);

        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 18);
        WriteFixedAscii(packet, 6, NameLength, "nobodyhome");
        WriteFixedAscii(packet, 30, NameLength, "whatever");
        packet[54] = 0;

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(26);

        Assert.Equal(AcRefuseLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4)));
    }

    /// <summary>
    /// The stock login screen is authentication-only: no login packet can create
    /// an account. The legacy rAthena "log in as name_M/name_F to auto-create an
    /// account" feature has been removed entirely (not merely disabled by
    /// default), so a login for a name ending in _M/_F with no existing account
    /// is just an unknown-username failure like any other, and must not create
    /// an AthenaIdentityUser or an AthenaGameAccount as a side effect.
    /// </summary>
    [Fact]
    public async Task StockLogin_UsernameEndingInM_WithNoExistingAccount_IsUnknownUsername_AndCreatesNothing()
    {
        using var identity = new IdentityTestFixture();
        using var fixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);

        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 18);
        WriteFixedAscii(packet, 6, NameLength, "newplayer_M");
        WriteFixedAscii(packet, 30, NameLength, "whatever");
        packet[54] = 0;

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(26);

        Assert.Equal(AcRefuseLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4)));

        await using var db = identity.CreateDb();
        Assert.False(await db.Users.AnyAsync(u => u.UserName == "newplayer_M"), "No AthenaIdentityUser must be created for an auto-register-style login.");
        Assert.Equal(0, await db.GameAccounts.CountAsync());
    }

    /// <summary>
    /// A real, pre-existing username that happens to literally end in _M or _F
    /// (coincidence, not the legacy auto-register convention) must still
    /// authenticate exactly like any other username.
    /// </summary>
    [Fact]
    public async Task StockLogin_ExistingUsernameEndingInF_AuthenticatesNormally()
    {
        using var identity = new IdentityTestFixture();
        var provisioned = await identity.ProvisionAsync("realplayer_F", "correct-password", 'F');

        using var fixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);
        fixture.RegisterCharServer(1, "Chaos");

        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 18);
        WriteFixedAscii(packet, 6, NameLength, "realplayer_F");
        WriteFixedAscii(packet, 30, NameLength, "correct-password");
        packet[54] = 0;

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(96);

        Assert.Equal(AcAcceptLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(provisioned.RagnarokAccountId, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8, 4)));
    }

    [Fact]
    public async Task StockLogin_LockedOutAfterFailedAttempts_ReturnsAcRefuseLoginWithCode6()
    {
        using var identity = new IdentityTestFixture(maxFailedAccessAttempts: 3);
        await identity.ProvisionAsync("lockoutuser", "correct-password", 'M');

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        // Act: exhaust the allowed failed attempts with wrong passwords.
        for (var i = 0; i < 3; i++)
        {
            using var failFixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);
            var badPacket = new byte[55];
            BinaryPrimitives.WriteInt16LittleEndian(badPacket.AsSpan(0, 2), CaLogin);
            WriteFixedAscii(badPacket, 6, NameLength, "lockoutuser");
            WriteFixedAscii(badPacket, 30, NameLength, "wrong-password");
            await (Task)handlePacket!.Invoke(failFixture.Session, new object[] { CaLogin, badPacket, CancellationToken.None })!;
            await failFixture.ReadExactAsync(26);
        }

        // One more attempt, even with the correct password, must now be locked out.
        using var fixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);
        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        WriteFixedAscii(packet, 6, NameLength, "lockoutuser");
        WriteFixedAscii(packet, 30, NameLength, "correct-password");

        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(26);

        Assert.Equal(AcRefuseLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4)));
    }

    [Fact]
    public async Task StockLogin_SuccessfulLogin_ResetsFailedAttemptCount()
    {
        using var identity = new IdentityTestFixture(maxFailedAccessAttempts: 3);
        await identity.ProvisionAsync("resetuser", "correct-password", 'M');

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        // Two failures, then a success - the success must clear the failure count.
        for (var i = 0; i < 2; i++)
        {
            using var failFixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);
            var badPacket = new byte[55];
            BinaryPrimitives.WriteInt16LittleEndian(badPacket.AsSpan(0, 2), CaLogin);
            WriteFixedAscii(badPacket, 6, NameLength, "resetuser");
            WriteFixedAscii(badPacket, 30, NameLength, "wrong-password");
            await (Task)handlePacket!.Invoke(failFixture.Session, new object[] { CaLogin, badPacket, CancellationToken.None })!;
            await failFixture.ReadExactAsync(26);
        }

        using (var successFixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth))
        {
            successFixture.RegisterCharServer(1, "Chaos");
            var goodPacket = new byte[55];
            BinaryPrimitives.WriteInt16LittleEndian(goodPacket.AsSpan(0, 2), CaLogin);
            WriteFixedAscii(goodPacket, 6, NameLength, "resetuser");
            WriteFixedAscii(goodPacket, 30, NameLength, "correct-password");
            await (Task)handlePacket!.Invoke(successFixture.Session, new object[] { CaLogin, goodPacket, CancellationToken.None })!;
            await successFixture.ReadExactAsync(96);
        }

        // Two more failures (still under the limit of 3) must not lock the account out.
        for (var i = 0; i < 2; i++)
        {
            using var failFixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);
            var badPacket = new byte[55];
            BinaryPrimitives.WriteInt16LittleEndian(badPacket.AsSpan(0, 2), CaLogin);
            WriteFixedAscii(badPacket, 6, NameLength, "resetuser");
            WriteFixedAscii(badPacket, 30, NameLength, "wrong-password");
            await (Task)handlePacket!.Invoke(failFixture.Session, new object[] { CaLogin, badPacket, CancellationToken.None })!;
            var response = await failFixture.ReadExactAsync(26);
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4))); // still "invalid password", not locked out (6)
        }
    }

    [Fact]
    public async Task StockLogin_GameAccountMissingForIdentityUser_ReturnsAcRefuseLoginWithCode0()
    {
        using var identity = new IdentityTestFixture();
        await identity.CreateIdentityUserWithoutGameAccountAsync("orphanuser", "correct-password");

        using var fixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);

        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        WriteFixedAscii(packet, 6, NameLength, "orphanuser");
        WriteFixedAscii(packet, 30, NameLength, "correct-password");

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(26);

        Assert.Equal(AcRefuseLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4)));
    }

    [Fact]
    public async Task StockLogin_BannedGameAccount_ReturnsAcRefuseLoginWithCode6()
    {
        using var identity = new IdentityTestFixture();
        var provisioned = await identity.ProvisionAsync("banneduser", "correct-password", 'M');
        await identity.SetUnbanTimeAsync(provisioned.GameAccountId, DateTimeOffset.UtcNow.AddDays(1));

        using var fixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);
        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        WriteFixedAscii(packet, 6, NameLength, "banneduser");
        WriteFixedAscii(packet, 30, NameLength, "correct-password");

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(26);

        Assert.Equal(AcRefuseLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4)));
    }

    [Fact]
    public async Task StockLogin_ExpiredGameAccount_ReturnsAcRefuseLoginWithCode2()
    {
        using var identity = new IdentityTestFixture();
        var provisioned = await identity.ProvisionAsync("expireduser", "correct-password", 'M');
        await identity.SetExpirationTimeAsync(provisioned.GameAccountId, DateTimeOffset.UtcNow.AddDays(-1));

        using var fixture = ClientSessionFixture.Create(dbFactory: () => CreateLoginDb(Guid.NewGuid().ToString()), playerAuth: identity.PlayerAuth);
        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        WriteFixedAscii(packet, 6, NameLength, "expireduser");
        WriteFixedAscii(packet, 30, NameLength, "correct-password");

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(26);

        Assert.Equal(AcRefuseLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4)));
    }

    [Fact]
    public async Task PlayerLogin_EnablesWebAuthToken_ThenGoesOffline_DisablesWebAuthToken()
    {
        // Arrange: a real Identity login with UseWebAuthToken enabled end-to-end.
        // Login enables the token on AthenaGameAccount; CharServer then reports
        // the player offline, which must disable it - also on AthenaGameAccount,
        // looked up by RagnarokAccountId.
        using var identity = new IdentityTestFixture(useWebAuthToken: true);
        var provisioned = await identity.ProvisionAsync("webtokenuser", "correct-password", 'M');

        var loginDbName = Guid.NewGuid().ToString();

        using var fixture = ClientSessionFixture.Create(
            dbFactory: () => CreateLoginDb(loginDbName),
            playerAuth: identity.PlayerAuth,
            identityDbFactory: identity.CreateDb,
            config: new LoginConfig { LogLogin = false, UseWebAuthToken = true, DisableWebTokenDelayMs = 0 });
        fixture.RegisterCharServer(1, "Chaos");

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handlePacket);

        // Act 1: player logs in through the stock 0x0064 path.
        var loginPacket = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(loginPacket.AsSpan(0, 2), CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(loginPacket.AsSpan(2, 4), 18);
        WriteFixedAscii(loginPacket, 6, NameLength, "webtokenuser");
        WriteFixedAscii(loginPacket, 30, NameLength, "correct-password");
        loginPacket[54] = 0;
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, loginPacket, CancellationToken.None })!;
        await fixture.ReadExactAsync(96);

        Assert.True(await identity.GetWebAuthTokenEnabledAsync(provisioned.GameAccountId), "A successful login must enable the WebAuthToken.");

        // Act 2: CharServer completes the HMAC-SHA256 service handshake, then
        // reports the player offline (LcSetAccountOffline is a ServiceOnlyPackets
        // packet - CharServer must authenticate first).
        await CompleteServiceHandshakeAsync(fixture, handlePacket!);

        var offlinePacket = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(offlinePacket.AsSpan(2, 4), provisioned.RagnarokAccountId);
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { PacketConstants.LcSetAccountOffline, offlinePacket, CancellationToken.None })!;

        // ScheduleDisableWebAuthTokenAsync is fire-and-forget from
        // HandleSetAccountOffline, so poll briefly for the eventual write.
        var disabled = await PollUntilAsync(
            async () => !await identity.GetWebAuthTokenEnabledAsync(provisioned.GameAccountId),
            TimeSpan.FromSeconds(3));
        Assert.True(disabled, "Going offline must disable the WebAuthToken on AthenaGameAccount.");
    }

    private static async Task<bool> PollUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return await condition();
    }

    /// <summary>
    /// Drives the LcServiceHello -&gt; LcServiceAuthChallenge -&gt; LcServiceAuthProof
    /// -&gt; LcServiceAuthResult HMAC-SHA256 handshake against a session under
    /// test, using the fixture's configured <see cref="ServiceToken"/>. See
    /// ClientSessionServiceAuthGate.test.cs for dedicated success/failure
    /// coverage of the handshake itself; this helper exists only so flows that
    /// need an authenticated CharServer connection as a prerequisite (e.g. the
    /// WebAuthToken offline-notification test) can reach that state.
    /// </summary>
    private static async Task CompleteServiceHandshakeAsync(ClientSessionFixture fixture, MethodInfo handlePacket)
    {
        var hello = new byte[2 + NameLength + 4 + 2 + ServerNameLength + 2 + 2];
        WriteFixedAscii(hello, 2, NameLength, ServiceId);
        IPAddress.Loopback.GetAddressBytes().CopyTo(hello, 2 + NameLength);
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(2 + NameLength + 4, 2), 6121);
        WriteFixedAscii(hello, 2 + NameLength + 4 + 2, ServerNameLength, "TestChar");
        await (Task)handlePacket.Invoke(fixture.Session, new object[] { LcServiceHello, hello, CancellationToken.None })!;

        var challengePacket = await fixture.ReadExactAsync(2 + ServiceNonceLength);
        Assert.Equal(LcServiceAuthChallenge, BinaryPrimitives.ReadInt16LittleEndian(challengePacket.AsSpan(0, 2)));
        var nonce = challengePacket.AsSpan(2, ServiceNonceLength).ToArray();

        var helloInfo = new ServiceHelloInfo(ServiceId, IPAddress.Loopback, 6121, "TestChar", 0, 0);
        var proof = ServiceAuthProofCalculator.ComputeProof(Convert.FromBase64String(ServiceToken), helloInfo, nonce);
        var proofPacket = new byte[2 + ServiceProofLength];
        proof.CopyTo(proofPacket, 2);
        await (Task)handlePacket.Invoke(fixture.Session, new object[] { LcServiceAuthProof, proofPacket, CancellationToken.None })!;

        var resultPacket = await fixture.ReadExactAsync(3);
        Assert.Equal(LcServiceAuthResult, BinaryPrimitives.ReadInt16LittleEndian(resultPacket.AsSpan(0, 2)));
        Assert.Equal((byte)0, resultPacket[2]);
    }

    private static void WriteFixedAscii(byte[] buffer, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, Math.Min(length, bytes.Length));
    }

    private static LoginDbContext CreateLoginDb(string name)
    {
        var options = new DbContextOptionsBuilder<LoginDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new LoginDbContext(options);
    }

    /// <summary>
    /// Real ASP.NET Core Identity + AthenaGameAccount stack backed by SQLite
    /// in-memory (not the EF Core InMemory provider - lockout/state updates here
    /// rely on ordinary relational SaveChanges semantics across multiple scoped
    /// DbContext instances resolving the SAME connection, which InMemory does not
    /// guarantee to share).
    /// </summary>
    private sealed class IdentityTestFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        public IPlayerAuthenticationService PlayerAuth { get; }

        public IdentityTestFixture(int maxFailedAccessAttempts = 5, bool useWebAuthToken = false)
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
                options.Lockout.MaxFailedAccessAttempts = maxFailedAccessAttempts;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<AthenaIdentityDbContext>();
            services.AddScoped<IRagnarokAccountIdAllocator, SqliteMaxPlusOneRagnarokAccountIdAllocator>();
            services.AddScoped<IPlayerAccountProvisioningService, PlayerAccountProvisioningService>();
            services.AddSingleton(new LoginConfigStore(new LoginConfig { UseWebAuthToken = useWebAuthToken }));
            services.AddSingleton<IPlayerAuthenticationService, IdentityPlayerAuthenticationService>();

            _serviceProvider = services.BuildServiceProvider();

            using var scope = _serviceProvider.CreateScope();
            scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>().Database.EnsureCreated();

            PlayerAuth = _serviceProvider.GetRequiredService<IPlayerAuthenticationService>();
        }

        /// <summary>
        /// A short-lived context sharing this fixture's SQLite connection, for
        /// wiring into ClientSessionFixture as an _identityDbFactory - mirrors how
        /// production code never ties an AthenaIdentityDbContext to a connection's
        /// lifetime.
        /// </summary>
        public AthenaIdentityDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<AthenaIdentityDbContext>().UseSqlite(_connection).Options;
            return new AthenaIdentityDbContext(options);
        }

        public async Task<bool> GetWebAuthTokenEnabledAsync(Guid gameAccountId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
            var account = await db.GameAccounts.AsNoTracking().SingleAsync(a => a.Id == gameAccountId);
            return account.WebAuthTokenEnabled;
        }

        public async Task<ProvisionPlayerAccountResult> ProvisionAsync(string userName, string password, char sex)
        {
            using var scope = _serviceProvider.CreateScope();
            var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
            var result = await provisioning.ProvisionAsync(userName, $"{userName}@example.com", password, sex, CancellationToken.None);
            Assert.True(result.Success, result.ErrorMessage);
            return result;
        }

        public async Task CreateIdentityUserWithoutGameAccountAsync(string userName, string password)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AthenaIdentityUser>>();
            var user = new AthenaIdentityUser { Id = Guid.NewGuid(), UserName = userName, Email = $"{userName}@example.com" };
            var result = await userManager.CreateAsync(user, password);
            Assert.True(result.Succeeded);
        }

        public async Task SetUnbanTimeAsync(Guid gameAccountId, DateTimeOffset unbanUntil)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
            var account = await db.GameAccounts.SingleAsync(a => a.Id == gameAccountId);
            account.UnbanTime = (uint)unbanUntil.ToUnixTimeSeconds();
            await db.SaveChangesAsync();
        }

        public async Task SetExpirationTimeAsync(Guid gameAccountId, DateTimeOffset expiration)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
            var account = await db.GameAccounts.SingleAsync(a => a.Id == gameAccountId);
            account.ExpirationTime = (uint)expiration.ToUnixTimeSeconds();
            await db.SaveChangesAsync();
        }

        public void Dispose()
        {
            _serviceProvider.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class ClientSessionFixture : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _testClient;
        private readonly TcpClient _serverSide;
        private readonly CharServerRegistry _charServers;

        public ClientSession Session { get; }

        private ClientSessionFixture(ClientSession session, CharServerRegistry charServers, TcpListener listener, TcpClient testClient, TcpClient serverSide)
        {
            Session = session;
            _charServers = charServers;
            _listener = listener;
            _testClient = testClient;
            _serverSide = serverSide;
        }

        public static ClientSessionFixture Create(
            Func<LoginDbContext?> dbFactory,
            IPlayerAuthenticationService? playerAuth = null,
            Func<AthenaIdentityDbContext?>? identityDbFactory = null,
            LoginConfig? config = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var testClient = new TcpClient();
            var connectTask = testClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            var serverSide = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();

            // LogLogin is disabled because the legacy login-log write uses raw SQL
            // (ExecuteSqlRawAsync), which the EF Core InMemory provider used for the
            // service-account tests in this file does not support.
            var configStore = new LoginConfigStore(config ?? new LoginConfig { LogLogin = false });
            var messageStore = new LoginMessageStore(new LoginMessageCatalog(new Dictionary<uint, string>
            {
                [22] = "Unknown Error.",
            }));
            var charServers = new CharServerRegistry();
            var session = new ClientSession(
                serverSide,
                configStore,
                messageStore,
                dbFactory,
                identityDbFactory ?? (() => null),
                charServers,
                new LoginState(),
                new SubnetConfig(),
                playerAuth ?? new UnavailablePlayerAuthenticationService(),
                new ServiceAuthenticationService(new CharServerServiceTokenProvider(new SecretConfig { CharServerServiceToken = ServiceToken })));

            return new ClientSessionFixture(session, charServers, listener, testClient, serverSide);
        }

        public void RegisterCharServer(int id, string name)
        {
            _charServers.Register(id, new CharServerInfo
            {
                Name = name,
                Ip = IPAddress.Loopback,
                Port = 6121,
                Users = 0,
                Type = 0,
                IsNew = 0,
                Connection = null,
            });
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
