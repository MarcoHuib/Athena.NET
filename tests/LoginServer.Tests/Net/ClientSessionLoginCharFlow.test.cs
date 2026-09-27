using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Db.Entities;
using Athena.Net.LoginServer.Net;
using Microsoft.EntityFrameworkCore;

namespace Athena.Net.LoginServer.Tests.Net;

/// <summary>
/// End-to-end regression coverage for the existing (pre-Identity-migration) stock
/// Login -&gt; Char handoff: a real player login over the wire, and a real CharServer
/// service-account login over the wire, both backed by a real (in-memory) LoginDbContext.
/// This is the "golden path" the ASP.NET Core Identity migration must keep working.
/// </summary>
public sealed class ClientSessionLoginCharFlowTests
{
    private const short CaLogin = 0x64;
    private const short LcCharServerLogin = 0x2710;
    private const short AcAcceptLogin = 0x0A4D;
    private const short LcCharServerLoginAck = 0x2711;
    private const int NameLength = 24;

    [Fact]
    public async Task StockLogin_ValidPlayerCredentials_ReturnsAcAcceptLogin()
    {
        // Arrange: a real player row in a real (in-memory) LoginDbContext.
        await using var db = CreateDb(nameof(StockLogin_ValidPlayerCredentials_ReturnsAcAcceptLogin));
        db.Accounts.Add(new LoginAccount
        {
            AccountId = 2000010,
            UserId = "playerone",
            UserPass = "correct-password",
            Sex = "M",
            GroupId = 0,
            State = 0,
        });
        await db.SaveChangesAsync();

        using var fixture = ClientSessionFixture.Create(() => CreateDb(nameof(StockLogin_ValidPlayerCredentials_ReturnsAcAcceptLogin)));
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
        // account's real RagnarokAccountId (see ai/iro-2026-wire.md).
        Assert.Equal(AcAcceptLogin, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(2000010u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8, 4)));
        Assert.Equal((byte)1, response[46]); // sex M -> 1
    }

    [Fact]
    public async Task StockLogin_WrongPassword_ReturnsAcRefuseLogin()
    {
        // Arrange
        await using var db = CreateDb(nameof(StockLogin_WrongPassword_ReturnsAcRefuseLogin));
        db.Accounts.Add(new LoginAccount
        {
            AccountId = 2000011,
            UserId = "playertwo",
            UserPass = "the-real-password",
            Sex = "M",
        });
        await db.SaveChangesAsync();

        using var fixture = ClientSessionFixture.Create(() => CreateDb(nameof(StockLogin_WrongPassword_ReturnsAcRefuseLogin)));

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
        Assert.Equal((short)0x083E, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2, 4)));
    }

    [Fact]
    public async Task CharServerLogin_ValidServiceAccount_RegistersAndAcknowledges()
    {
        // Arrange: a reserved service account (sex='S', account_id < 5), per
        // ai/login-server.md's documented CharServer provisioning convention.
        await using var db = CreateDb(nameof(CharServerLogin_ValidServiceAccount_RegistersAndAcknowledges));
        db.Accounts.Add(new LoginAccount
        {
            AccountId = 1,
            UserId = "charserver",
            UserPass = "service-secret",
            Sex = "S",
        });
        await db.SaveChangesAsync();

        using var fixture = ClientSessionFixture.Create(() => CreateDb(nameof(CharServerLogin_ValidServiceAccount_RegistersAndAcknowledges)));

        var packet = BuildCharServerLoginPacket("charserver", "service-secret");

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        // Act
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { LcCharServerLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(3);

        // Assert: LcCharServerLoginAck with result 0 (success).
        Assert.Equal(LcCharServerLoginAck, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal((byte)0, response[2]);
    }

    [Fact]
    public async Task CharServerLogin_PlayerAccount_IsRejectedAsServer()
    {
        // Arrange: a normal player account must never be usable as a service login,
        // even with a correct password (ServerAccountAuthentication.Classify).
        await using var db = CreateDb(nameof(CharServerLogin_PlayerAccount_IsRejectedAsServer));
        db.Accounts.Add(new LoginAccount
        {
            AccountId = 2000020,
            UserId = "notaserver",
            UserPass = "whatever",
            Sex = "M",
        });
        await db.SaveChangesAsync();

        using var fixture = ClientSessionFixture.Create(() => CreateDb(nameof(CharServerLogin_PlayerAccount_IsRejectedAsServer)));
        var packet = BuildCharServerLoginPacket("notaserver", "whatever");

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        // Act
        await (Task)handlePacket!.Invoke(fixture.Session, new object[] { LcCharServerLogin, packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(3);

        // Assert
        Assert.Equal(LcCharServerLoginAck, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(0, 2)));
        Assert.Equal((byte)3, response[2]);
    }

    private static byte[] BuildCharServerLoginPacket(string user, string pass)
    {
        // LcCharServerLogin is 86 bytes: id[2] user[24] pass[24] ... ip[4] port[2] name[20] type[2] isNew[2] (from offset 54).
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

    private static LoginDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<LoginDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new LoginDbContext(options);
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

        public static ClientSessionFixture Create(Func<LoginDbContext?> dbFactory)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var testClient = new TcpClient();
            var connectTask = testClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            var serverSide = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();

            // LogLogin is disabled because the legacy login-log write uses raw SQL
            // (ExecuteSqlRawAsync), which the EF Core InMemory provider used by these
            // tests does not support.
            var configStore = new LoginConfigStore(new LoginConfig { LogLogin = false });
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
                charServers,
                new LoginState(),
                new SubnetConfig());

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
