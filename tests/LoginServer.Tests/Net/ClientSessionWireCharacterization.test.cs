using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Tests.Net;

/// <summary>
/// Characterization tests pinning the verified stock-iRO LoginServer wire behavior
/// (see ai/iro-2026-wire.md) so the upcoming ASP.NET Core Identity migration cannot
/// silently change 0x0064 parsing or 0x0A4D serialization.
/// </summary>
public sealed class ClientSessionWireCharacterizationTests
{
    private const short CaLogin = 0x64;
    private const short AcAcceptLogin = 0x0A4D;
    private const int NameLength = 24;

    [Fact]
    public void ParsePlainLogin_Parses0x0064_ExactFieldOffsets()
    {
        // Arrange: exact verified stock-iRO 0x0064 layout (ai/iro-2026-wire.md):
        // packet id[2] version[4] username[24] password[24] clientType[1] = 55 bytes.
        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 18);
        WriteFixedAscii(packet, 6, NameLength, "bobby");
        WriteFixedAscii(packet, 30, NameLength, "hunter2");
        packet[54] = 7;

        using var fixture = ClientSessionFixture.Create();
        var method = typeof(ClientSession).GetMethod("ParsePlainLogin", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Act
        var request = method!.Invoke(fixture.Session, new object[] { packet })!;
        var requestType = request.GetType();

        // Assert
        Assert.Equal("bobby", (string)requestType.GetProperty("UserId")!.GetValue(request)!);
        Assert.Equal("hunter2", (string)requestType.GetProperty("Password")!.GetValue(request)!);
        Assert.Equal((byte)7, (byte)requestType.GetProperty("ClientType")!.GetValue(request)!);
    }

    [Fact]
    public void ParsePlainLogin_StopsUsernameAndPassword_AtFirstNulByte()
    {
        // Arrange: garbage after the first NUL byte must never be part of the parsed value.
        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 18);
        WriteFixedAscii(packet, 6, NameLength, "ab");
        packet[6 + 2] = 0;
        Encoding.ASCII.GetBytes("TRAILING-GARBAGE").CopyTo(packet, 6 + 3);
        WriteFixedAscii(packet, 30, NameLength, "pw");
        packet[30 + 2] = 0;
        Encoding.ASCII.GetBytes("MORE-GARBAGE").CopyTo(packet, 30 + 3);
        packet[54] = 0;

        using var fixture = ClientSessionFixture.Create();
        var method = typeof(ClientSession).GetMethod("ParsePlainLogin", BindingFlags.NonPublic | BindingFlags.Instance);

        // Act
        var request = method!.Invoke(fixture.Session, new object[] { packet })!;
        var requestType = request.GetType();

        // Assert
        Assert.Equal("ab", (string)requestType.GetProperty("UserId")!.GetValue(request)!);
        Assert.Equal("pw", (string)requestType.GetProperty("Password")!.GetValue(request)!);
    }

    [Fact]
    public async Task SendAcceptLoginAsync_NoCharServers_Produces64ByteHeaderOnly()
    {
        // Arrange: verified 0x0A4D layout is a 64-byte login header plus 32-byte
        // world entries (ai/iro-2026-wire.md). With no registered char servers the
        // response must be exactly the 64-byte header.
        using var fixture = ClientSessionFixture.Create();
        var authResult = fixture.CreateAuthResult(
            accountId: 2000001,
            loginId1: 111,
            loginId2: 222,
            sex: 1,
            webAuthToken: string.Empty);

        // Act
        var bytes = await fixture.InvokeSendAcceptLoginAsync(authResult, expectedLength: 64);

        // Assert
        Assert.Equal(64, bytes.Length);
        Assert.Equal(AcAcceptLogin, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(0, 2)));
        Assert.Equal((short)64, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2, 2)));
        Assert.Equal(111u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        Assert.Equal(2000001u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(222u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)));
        Assert.Equal((byte)1, bytes[46]);
    }

    [Fact]
    public async Task SendAcceptLoginAsync_ThreeCharServers_Produces160ByteResponse()
    {
        // Arrange: reproduces the captured three-world (Chaos/Thor/Freya) 160-byte
        // response documented in ai/iro-2026-wire.md as 64-byte header + 3*32-byte entries.
        using var fixture = ClientSessionFixture.Create(new LoginConfig { UseWebAuthToken = true });
        fixture.RegisterCharServer(1, "Chaos");
        fixture.RegisterCharServer(2, "Thor");
        fixture.RegisterCharServer(3, "Freya");

        var authResult = fixture.CreateAuthResult(
            accountId: 2000005,
            loginId1: 10,
            loginId2: 20,
            sex: 0,
            webAuthToken: "abcdef0123456789");

        // Act
        var bytes = await fixture.InvokeSendAcceptLoginAsync(authResult, expectedLength: 160);

        // Assert
        Assert.Equal(160, bytes.Length);
        Assert.Equal(AcAcceptLogin, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(0, 2)));
        Assert.Equal((short)160, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2, 2)));
        Assert.Equal((byte)0, bytes[46]);

        var tokenBytes = Encoding.ASCII.GetBytes("abcdef0123456789");
        Assert.Equal(tokenBytes, bytes.AsSpan(47, tokenBytes.Length).ToArray());

        // Each 32-byte world entry: ip[4] port[2] name[20] usercount[2] type[2] isNew[2].
        for (var i = 0; i < 3; i++)
        {
            var offset = 64 + i * 32;
            var ip = new IPAddress(bytes.AsSpan(offset, 4).ToArray());
            Assert.Equal(fixture.Config.IroAdvertisedCharIp, ip);
            var port = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4, 2));
            Assert.Equal((ushort)fixture.Config.IroAdvertisedCharPort, port);

            // Existing (pre-migration) behavior: every world entry's name is
            // hardcoded to "Chaos" regardless of the registered char server's own
            // Name. This is characterized as-is, not endorsed as correct, and must
            // not change silently during the Identity migration.
            var nameSpan = bytes.AsSpan(offset + 6, 20);
            var nullIndex = nameSpan.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(nullIndex >= 0 ? nameSpan[..nullIndex] : nameSpan);
            Assert.Equal("Chaos", name);
        }
    }

    [Fact]
    public async Task HandleLoginAsync_FailedLogin_NeverLogsPlaintextPassword()
    {
        // Arrange: exercise the full failure path (no DB configured -> immediate
        // AuthResult.Fail) with a distinctive password and assert it never reaches
        // stdout/log output.
        const string secretPassword = "sUp3rSecretPw9";
        using var fixture = ClientSessionFixture.Create();

        var packet = new byte[55];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 18);
        WriteFixedAscii(packet, 6, NameLength, "someone");
        WriteFixedAscii(packet, 30, NameLength, secretPassword);
        packet[54] = 0;

        var handlePacket = typeof(ClientSession).GetMethod("HandlePacketAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handlePacket);

        var originalOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            // Act
            var task = (Task)handlePacket!.Invoke(fixture.Session, new object[] { CaLogin, packet, CancellationToken.None })!;
            await task;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Assert
        Assert.DoesNotContain(secretPassword, captured.ToString(), StringComparison.Ordinal);
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
        private readonly CharServerRegistry _charServers;

        public ClientSession Session { get; }
        public LoginConfig Config { get; }

        private ClientSessionFixture(
            ClientSession session,
            LoginConfig config,
            CharServerRegistry charServers,
            TcpListener listener,
            TcpClient testClient,
            TcpClient serverSide)
        {
            Session = session;
            Config = config;
            _charServers = charServers;
            _listener = listener;
            _testClient = testClient;
            _serverSide = serverSide;
        }

        public static ClientSessionFixture Create(LoginConfig? config = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var testClient = new TcpClient();
            var connectTask = testClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            var serverSide = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();

            config ??= new LoginConfig();
            var configStore = new LoginConfigStore(config);
            var messageStore = new LoginMessageStore(new LoginMessageCatalog(new Dictionary<uint, string>
            {
                [22] = "Unknown Error.",
            }));
            var charServers = new CharServerRegistry();
            var session = new ClientSession(
                serverSide,
                configStore,
                messageStore,
                () => null,
                charServers,
                new LoginState(),
                new SubnetConfig());

            return new ClientSessionFixture(session, config, charServers, listener, testClient, serverSide);
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

        public object CreateAuthResult(uint accountId, uint loginId1, uint loginId2, byte sex, string webAuthToken)
        {
            var authResultType = typeof(ClientSession).GetNestedType("AuthResult", BindingFlags.NonPublic);
            Assert.NotNull(authResultType);

            return Activator.CreateInstance(
                authResultType!,
                true,
                0u,
                string.Empty,
                accountId,
                loginId1,
                loginId2,
                sex,
                0,
                webAuthToken,
                0u)!;
        }

        public async Task<byte[]> InvokeSendAcceptLoginAsync(object authResult, int expectedLength)
        {
            var method = typeof(ClientSession).GetMethod("SendAcceptLoginAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(Session, new object?[] { authResult, null, CancellationToken.None })!;
            await task;

            return await ReadExactAsync(expectedLength);
        }

        private async Task<byte[]> ReadExactAsync(int length)
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
