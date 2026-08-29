using System.Net;
using System.Net.Sockets;
using Athena.Net.Launcher.Core;
using Athena.Net.Launcher.Networking;

namespace Athena.Net.Launcher.Networking.Tests;

public sealed class TcpProxyTests
{
    [Fact]
    public async Task TcpProxyCopiesBytesInBothDirections()
    {
        var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var backendPort = ((IPEndPoint)backend.LocalEndpoint).Port;
        var listenPort = FreePort();
        await using var proxy = new TcpProxy(new ProxyEndpoint("test", IPAddress.Loopback, listenPort, "127.0.0.1", backendPort), new NullLog());
        await proxy.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        using var server = await backend.AcceptTcpClientAsync();
        var clientBytes = new byte[] { 1, 2, 3, 4 };
        await client.GetStream().WriteAsync(clientBytes);
        var received = new byte[4];
        await server.GetStream().ReadExactlyAsync(received);
        Assert.Equal(clientBytes, received);
        var serverBytes = new byte[] { 8, 7, 6 };
        await server.GetStream().WriteAsync(serverBytes);
        received = new byte[3];
        await client.GetStream().ReadExactlyAsync(received);
        Assert.Equal(serverBytes, received);
        backend.Stop();
    }

    [Fact]
    public async Task PartialProxyStartupRollsBackEveryProxy()
    {
        var created = new List<FakeProxy>();
        var manager = new TcpProxyManager(new NullLog(), endpoint =>
        {
            var proxy = new FakeProxy(endpoint.Name == "second");
            created.Add(proxy);
            return proxy;
        });
        var endpoints = new[]
        {
            new ProxyEndpoint("first", IPAddress.Loopback, FreePort(), "localhost", 1),
            new ProxyEndpoint("second", IPAddress.Loopback, FreePort(), "localhost", 2),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(endpoints, CancellationToken.None));
        Assert.All(created, proxy => Assert.True(proxy.Stopped));
        Assert.Empty(manager.Endpoints);
    }

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }

    private sealed class FakeProxy(bool fail) : ITcpProxy
    {
        public ProxyState State { get; private set; }
        public bool Stopped { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { if (fail) throw new InvalidOperationException("fail"); State = ProxyState.Running; return Task.CompletedTask; }
        public Task StopAsync() { Stopped = true; State = ProxyState.Stopped; return Task.CompletedTask; }
        public async ValueTask DisposeAsync() => await StopAsync();
    }

    private sealed class NullLog : ILauncherLog
    {
        public void Information(string e, string m, IReadOnlyDictionary<string, object?>? p = null) { }
        public void Error(string e, Exception x, string m) { }
    }
}
