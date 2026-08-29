using Athena.Net.Launcher.Core;
using System.Net;
using System.Net.NetworkInformation;

namespace Athena.Net.Launcher.Networking;

public sealed class TcpProxyManager : ITcpProxyManager
{
    private readonly ILauncherLog _log;
    private readonly Func<ProxyEndpoint, ITcpProxy> _factory;
    private readonly List<ITcpProxy> _proxies = [];
    public IReadOnlyList<ProxyEndpoint> Endpoints { get; private set; } = [];

    public TcpProxyManager(ILauncherLog log, Func<ProxyEndpoint, ITcpProxy>? factory = null)
    {
        _log = log;
        _factory = factory ?? (endpoint => new TcpProxy(endpoint, log));
    }

    public void ValidateAvailable(IReadOnlyList<ProxyEndpoint> endpoints)
    {
        LauncherCoordinator.ValidateUniqueEndpoints(endpoints);
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        foreach (var endpoint in endpoints)
        {
            if (active.Any(x => x.Port == endpoint.ListenPort &&
                (x.Address.Equals(IPAddress.Any) || x.Address.Equals(IPAddress.IPv6Any) || x.Address.Equals(endpoint.ListenAddress))))
            {
                throw new InvalidOperationException($"Required proxy listener endpoint {endpoint.ListenEndPoint} is already in use.");
            }
        }
    }

    public async Task StartAsync(IReadOnlyList<ProxyEndpoint> endpoints, CancellationToken cancellationToken)
    {
        if (_proxies.Count != 0) throw new InvalidOperationException("Proxy manager is already running.");
        ValidateAvailable(endpoints);
        Endpoints = endpoints.ToArray();
        try
        {
            foreach (var endpoint in endpoints)
            {
                var proxy = _factory(endpoint);
                _proxies.Add(proxy);
                await proxy.StartAsync(cancellationToken);
            }
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        List<Exception>? failures = null;
        for (var index = _proxies.Count - 1; index >= 0; index--)
        {
            try { await _proxies[index].DisposeAsync(); }
            catch (Exception ex) { (failures ??= []).Add(ex); _log.Error("proxy.stop.failed", ex, "Proxy stop failed."); }
        }
        _proxies.Clear();
        Endpoints = [];
        if (failures != null) throw new AggregateException(failures);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
