using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionLifecycleTests
{
    [Fact]
    public async Task RunAsync_ContinuesAfterIro007dUntilRemoteDisconnect()
    {
        using var fixture = await SessionFixture.CreateAsync();
        await using var session = fixture.CreateIroSession();
        var runTask = session.RunAsync(CancellationToken.None);

        await fixture.ClientStream.WriteAsync(
            new byte[]
            {
                0x7d, 0x00, 0xba,
                0x60, 0x03, 0xf8, 0xcb, 0xde, 0x04, 0xab,
                0xc9, 0x08, 0x90,
                0x1c, 0x0b,
            });

        var response = new byte[2];
        await fixture.ClientStream.ReadExactlyAsync(response);
        Assert.Equal(new byte[] { 0x1d, 0x0b }, response);
        Assert.False(runTask.IsCompleted);

        fixture.Client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_ServerCancellationStopsBlockedReadCleanly()
    {
        using var fixture = await SessionFixture.CreateAsync();
        await using var session = fixture.CreateIroSession();
        using var cancellation = new CancellationTokenSource();
        var runTask = session.RunAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StopAsync_IsIdempotentAndSharedAcrossCallers()
    {
        using var fixture = await SessionFixture.CreateAsync();
        var session = fixture.CreateIroSession();

        // StopAsync/DisposeAsync must be safe to call multiple times and concurrently: every caller
        // observes the SAME shutdown Task rather than each racing its own teardown of the same
        // resources (the defect the earlier synchronous Dispose() had).
        var first = session.StopAsync();
        var second = session.StopAsync();
        await using var _ = session;

        await first;
        await second;
        Assert.Same(first, second);
    }

    private sealed class SessionFixture : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _serverClient;

        private SessionFixture(TcpListener listener, TcpClient client, TcpClient serverClient)
        {
            _listener = listener;
            Client = client;
            _serverClient = serverClient;
            ClientStream = client.GetStream();
        }

        public TcpClient Client { get; }
        public NetworkStream ClientStream { get; }

        public static async Task<SessionFixture> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var client = new TcpClient();
            var connectTask = client.ConnectAsync(endpoint.Address, endpoint.Port);
            var serverClient = await listener.AcceptTcpClientAsync();
            await connectTask;
            return new SessionFixture(listener, client, serverClient);
        }

        public MapClientSession CreateIroSession()
        {
            var configStore = new MapConfigStore(new MapConfig(), "unused.conf");
            var connector = new CharServerConnector(configStore);
            return new MapClientSession(1, _serverClient, connector, iroAuthenticated: true);
        }

        public void Dispose()
        {
            ClientStream.Dispose();
            Client.Dispose();
            _serverClient.Dispose();
            _listener.Stop();
        }
    }
}
