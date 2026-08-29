using System.Collections.Concurrent;
using System.Net.Sockets;
using Athena.Net.Launcher.Core;

namespace Athena.Net.Launcher.Networking;

public sealed class TcpProxy : ITcpProxy
{
    private readonly ProxyEndpoint _endpoint;
    private readonly ILauncherLog _log;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;
    private long _connectionId;

    public TcpProxy(ProxyEndpoint endpoint, ILauncherLog log) { _endpoint = endpoint; _log = log; }
    public ProxyState State { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (State != ProxyState.Stopped) throw new InvalidOperationException($"Proxy {_endpoint.Name} is already started.");
        State = ProxyState.Starting;
        try
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new TcpListener(_endpoint.ListenEndPoint);
            _listener.Start();
            State = ProxyState.Running;
            _log.Information("proxy.listener.started", $"{_endpoint.Name} proxy listener started.", new Dictionary<string, object?> { ["listen"] = _endpoint.ListenEndPoint.ToString(), ["target"] = $"{_endpoint.TargetHost}:{_endpoint.TargetPort}" });
            _acceptLoop = AcceptLoopAsync(_lifetime.Token);
            return Task.CompletedTask;
        }
        catch { State = ProxyState.Faulted; _listener?.Stop(); throw; }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient inbound;
            try { inbound = await _listener!.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.Error("proxy.accept.failed", ex, $"{_endpoint.Name} accept failed."); continue; }

            var id = Interlocked.Increment(ref _connectionId);
            var task = TunnelAsync(id, inbound, cancellationToken);
            _connections[id] = task;
            _ = task.ContinueWith(completed => _connections.TryRemove(id, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task TunnelAsync(long id, TcpClient inbound, CancellationToken cancellationToken)
    {
        using (inbound)
        using (var outbound = new TcpClient())
        using (var tunnelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            _log.Information("proxy.connection.accepted", $"{_endpoint.Name} accepted connection {id}.");
            try
            {
                await outbound.ConnectAsync(_endpoint.TargetHost, _endpoint.TargetPort, cancellationToken);
                _log.Information("proxy.connection.target", $"{_endpoint.Name} connected target for connection {id}.");
                await using var input = inbound.GetStream();
                await using var output = outbound.GetStream();
                var upstream = PumpAsync(input, output, tunnelCts.Token);
                var downstream = PumpAsync(output, input, tunnelCts.Token);
                await Task.WhenAny(upstream, downstream);
                await tunnelCts.CancelAsync();
                inbound.Close();
                outbound.Close();
                try { await Task.WhenAll(upstream, downstream); } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) { _log.Error("proxy.connection.failed", ex, $"{_endpoint.Name} connection {id} failed."); }
            finally { _log.Information("proxy.connection.closed", $"{_endpoint.Name} connection {id} closed."); }
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        await source.CopyToAsync(destination, 81920, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        if (State is ProxyState.Stopped or ProxyState.Stopping) return;
        State = ProxyState.Stopping;
        if (_lifetime != null) await _lifetime.CancelAsync();
        _listener?.Stop();
        if (_acceptLoop != null) try { await _acceptLoop; } catch (OperationCanceledException) { }
        var connections = _connections.Values.ToArray();
        if (connections.Length > 0) await Task.WhenAll(connections);
        _lifetime?.Dispose();
        _lifetime = null;
        _listener = null;
        State = ProxyState.Stopped;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
