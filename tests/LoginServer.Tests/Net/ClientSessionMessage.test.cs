using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Tests.Net;

public sealed class ClientSessionMessageTests
{
    [Fact]
    public void ResolveLoginMessage_UsesExplicitMessage()
    {
        // Arrange
        var catalog = new LoginMessageCatalog(new Dictionary<uint, string>
        {
            [3] = "Rejected from server.",
            [22] = "Unknown Error.",
        });

        using var fixture = ClientSessionFixture.Create(catalog);
        var method = typeof(ClientSession).GetMethod("ResolveLoginMessage", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Act
        var result = (string)method!.Invoke(fixture.Session, new object[] { 3u, "custom" })!;

        // Assert
        Assert.Equal("custom", result);
    }

    [Fact]
    public void ResolveLoginMessage_MapsLegacyOffsetRange()
    {
        // Arrange
        var catalog = new LoginMessageCatalog(new Dictionary<uint, string>
        {
            [16] = "Account gone.",
            [22] = "Unknown Error.",
        });

        using var fixture = ClientSessionFixture.Create(catalog);
        var method = typeof(ClientSession).GetMethod("ResolveLoginMessage", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Act
        var result = (string)method!.Invoke(fixture.Session, new object[] { 99u, string.Empty })!;

        // Assert
        Assert.Equal("Account gone.", result);
    }

    [Fact]
    public void ResolveLoginMessage_FallsBackToUnknown()
    {
        // Arrange
        var catalog = new LoginMessageCatalog(new Dictionary<uint, string>
        {
            [22] = "Unknown Error.",
        });

        using var fixture = ClientSessionFixture.Create(catalog);
        var method = typeof(ClientSession).GetMethod("ResolveLoginMessage", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Act
        var result = (string)method!.Invoke(fixture.Session, new object[] { 250u, string.Empty })!;

        // Assert
        Assert.Equal("Unknown Error.", result);
    }

    [Fact]
    public void FormatDate_UsesLoginConfigFormat()
    {
        // Arrange
        using var fixture = ClientSessionFixture.Create(new LoginMessageCatalog(new Dictionary<uint, string>()), "yyyy-MM-dd HH:mm:ss");
        var method = typeof(ClientSession).GetMethod("FormatDate", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var time = new DateTime(2026, 1, 18, 10, 20, 30, DateTimeKind.Local);

        // Act
        var result = (string)method!.Invoke(fixture.Session, new object[] { time })!;

        // Assert
        Assert.Equal("2026-01-18 10:20:30", result);
    }

    private sealed class ClientSessionFixture : IDisposable
    {
        public ClientSession Session { get; }
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly TcpClient _serverSide;

        private ClientSessionFixture(ClientSession session, TcpListener listener, TcpClient client, TcpClient serverSide)
        {
            Session = session;
            _listener = listener;
            _client = client;
            _serverSide = serverSide;
        }

        public static ClientSessionFixture Create(LoginMessageCatalog catalog, string? dateFormat = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var client = new TcpClient();
            var connectTask = client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            var serverSide = listener.AcceptTcpClient();
            connectTask.GetAwaiter().GetResult();

            var config = new LoginConfig
            {
                DateFormat = dateFormat ?? "yyyy-MM-dd HH:mm:ss",
            };
            var configStore = new LoginConfigStore(config);
            var messageStore = new LoginMessageStore(catalog);
            var session = new ClientSession(
                serverSide,
                configStore,
                messageStore,
                () => null,
                new CharServerRegistry(),
                new LoginState(),
                new SubnetConfig());

            return new ClientSessionFixture(session, listener, client, serverSide);
        }

        public void Dispose()
        {
            Session.Dispose();
            _serverSide.Dispose();
            _client.Dispose();
            _listener.Stop();
        }
    }
}
