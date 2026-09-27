using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Tests.Net;

/// <summary>
/// Characterization tests for the existing LoginServer -> CharServer AuthNode
/// handoff (LcAuthRequest/LcAuthResponse). These pin the current legacy
/// LoginId1/LoginId2/AuthNode semantics so the ASP.NET Core Identity migration
/// cannot silently change the Login -> Char handoff contract.
/// </summary>
public sealed class ClientSessionAuthRequestCharacterizationTests
{
    private const short LcAuthResponse = 0x2713;

    [Fact]
    public async Task HandleAuthRequestAsync_MatchingAuthNode_SucceedsAndIsConsumedOnce()
    {
        // Arrange
        var state = new LoginState();
        state.AddAuthNode(new AuthNode
        {
            AccountId = 2000042,
            LoginId1 = 111111,
            LoginId2 = 222222,
            Sex = 1,
            ClientType = 5,
            Ip = 0,
        });

        using var fixture = ClientSessionFixture.Create(state);
        var packet = BuildAuthRequestPacket(2000042, 111111, 222222, sex: 1, requestId: 999);

        var method = typeof(ClientSession).GetMethod("HandleAuthRequestAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Act: first request must succeed and consume the node.
        await (Task)method!.Invoke(fixture.Session, new object[] { packet, CancellationToken.None })!;
        var firstResponse = await fixture.ReadExactAsync(21);

        // Assert: LcAuthResponse layout is id[2] accountId[4] loginId1[4] loginId2[4] sex[1] result[1] requestId[4] clientType[1] = 21 bytes.
        Assert.Equal(LcAuthResponse, BinaryPrimitives.ReadInt16LittleEndian(firstResponse.AsSpan(0, 2)));
        Assert.Equal(2000042u, BinaryPrimitives.ReadUInt32LittleEndian(firstResponse.AsSpan(2, 4)));
        Assert.Equal(111111u, BinaryPrimitives.ReadUInt32LittleEndian(firstResponse.AsSpan(6, 4)));
        Assert.Equal(222222u, BinaryPrimitives.ReadUInt32LittleEndian(firstResponse.AsSpan(10, 4)));
        Assert.Equal((byte)1, firstResponse[14]);
        Assert.Equal((byte)0, firstResponse[15]); // result: 0 = success
        Assert.Equal(999u, BinaryPrimitives.ReadUInt32LittleEndian(firstResponse.AsSpan(16, 4)));
        Assert.Equal((byte)5, firstResponse[20]); // clientType carried from the consumed AuthNode

        Assert.False(state.TryGetAuthNode(2000042, out _));

        // Act: a second identical request must fail because the node was already consumed.
        await (Task)method!.Invoke(fixture.Session, new object[] { packet, CancellationToken.None })!;
        var secondResponse = await fixture.ReadExactAsync(21);

        // Assert
        Assert.Equal((byte)1, secondResponse[15]); // result: 1 = failure (node already consumed)
    }

    [Fact]
    public async Task HandleAuthRequestAsync_MismatchedLoginId_Fails()
    {
        // Arrange
        var state = new LoginState();
        state.AddAuthNode(new AuthNode
        {
            AccountId = 5,
            LoginId1 = 1,
            LoginId2 = 2,
            Sex = 0,
            ClientType = 0,
            Ip = 0,
        });

        using var fixture = ClientSessionFixture.Create(state);
        var packet = BuildAuthRequestPacket(accountId: 5, loginId1: 1, loginId2: 999 /* wrong */, sex: 0, requestId: 1);

        var method = typeof(ClientSession).GetMethod("HandleAuthRequestAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        // Act
        await (Task)method!.Invoke(fixture.Session, new object[] { packet, CancellationToken.None })!;
        var response = await fixture.ReadExactAsync(21);

        // Assert: mismatched tuple never succeeds and the node stays intact (not consumed).
        Assert.Equal((byte)1, response[15]);
        Assert.True(state.TryGetAuthNode(5, out _));
    }

    private static byte[] BuildAuthRequestPacket(uint accountId, uint loginId1, uint loginId2, byte sex, uint requestId)
    {
        var packet = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6, 4), loginId1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10, 4), loginId2);
        packet[14] = sex;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(19, 4), requestId);
        return packet;
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

        public static ClientSessionFixture Create(LoginState state)
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
                new CharServerRegistry(),
                state,
                new SubnetConfig());

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
