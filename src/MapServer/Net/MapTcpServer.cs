using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Telemetry;

namespace Athena.Net.MapServer.Net;

public sealed class MapTcpServer
{
    private readonly MapConfigStore _configStore;
    private readonly CharServerConnector _charConnector;
    private readonly TcpListener _listener;
    private int _nextSessionId;

    public MapTcpServer(MapConfigStore configStore, CharServerConnector charConnector)
    {
        _configStore = configStore;
        _charConnector = charConnector;
        var config = _configStore.Current;
        _listener = new TcpListener(config.BindIp, config.MapPort);
    }

    public int BoundPort { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        MapLogger.Status($"Map server listening on {_configStore.Current.BindIp}:{BoundPort}...");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                var sessionId = Interlocked.Increment(ref _nextSessionId);
                _ = HandleClientAsync(sessionId, client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(int sessionId, TcpClient client, CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
        MapTelemetry.ConnectionsAccepted.Add(1);
        using var activity = MapTelemetry.ActivitySource.StartActivity("map.client.session", ActivityKind.Server);
        activity?.SetTag("net.peer.ip", endpoint?.Address.ToString());
        activity?.SetTag("net.peer.port", endpoint?.Port);
        MapLogger.Info($"Client connected: {endpoint}");

        using (client)
        using (var session = new MapClientSession(sessionId, client, _charConnector))
        {
            try
            {
                await session.RunAsync(cancellationToken);
            }
            catch (IOException)
            {
                // Client disconnected.
            }
            catch (OperationCanceledException)
            {
                // Server shutdown.
            }
            catch (Exception ex)
            {
                MapLogger.Warning($"Client session error: {ex.Message}");
            }
        }

        MapLogger.Info($"Client disconnected: {endpoint}");
    }
}
