using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Telemetry;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

public sealed class MapTcpServer
{
    // MonsterRuntime.ProcessTick's own doc comment: it expects to be invoked periodically "at a
    // cadence far shorter than WalkSpeed so movement still looks smooth, not once per pinned-exact-
    // cell-duration". WalkSpeed values in pinned mob_db.yml are on the order of hundreds of ms
    // (G_PORING=400); 100ms gives several notification opportunities per cell without flooding
    // connected clients on every server loop iteration.
    private static readonly TimeSpan MonsterTickInterval = TimeSpan.FromMilliseconds(100);

    private readonly MapConfigStore _configStore;
    private readonly CharServerConnector _charConnector;
    private readonly MapServerWorld _world;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<int, MapClientSession> _sessions = new();
    private int _nextSessionId;

    public MapTcpServer(MapConfigStore configStore, CharServerConnector charConnector, MapServerWorld world)
    {
        _configStore = configStore;
        _charConnector = charConnector;
        _world = world;
        var config = _configStore.Current;
        _listener = new TcpListener(config.BindIp, config.MapPort);
    }

    public int BoundPort { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        MapLogger.Status($"Map server listening on {_configStore.Current.BindIp}:{BoundPort}...");
        MapLogger.Status(
            $"WORLD: loaded {_world.Maps.EntityCount} world entities over {_world.Maps.MapCount} maps, {_world.Maps.StaticWarpCount} active warps, {_world.Maps.DynamicWarpActorCount} legacy dynamic/scripted warp actors, {_world.Monsters.AllInstances.Count} monster instances.");

        var monsterTickLoop = RunMonsterTickLoopAsync(cancellationToken);

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
            await monsterTickLoop;
        }
    }

    // The single shared driver for both monster respawn processing and monster AI/movement
    // (MonsterRegistry.ProcessDueRespawns / MonsterRuntime.ProcessTick) - see those methods' own
    // doc comments for why neither may be driven by a per-monster Timer/Task. Every connected
    // session observes the SAME authoritative MobInstance state from this ONE loop, matching the
    // task requirement that monster movement seen by different players originates from one source.
    private async Task RunMonsterTickLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(MonsterTickInterval, cancellationToken);

                _world.Monsters.ProcessDueRespawns();
                var changed = _world.MonsterRuntime.ProcessTick();
                if (changed.Count == 0) continue;

                foreach (var session in _sessions.Values)
                {
                    foreach (var instance in changed)
                    {
                        try
                        {
                            await session.NotifyMonsterMovedAsync(instance, cancellationToken);
                        }
                        catch (IOException)
                        {
                            // Client disconnected; HandleClientAsync's own cleanup removes it from _sessions.
                        }
                        catch (OperationCanceledException)
                        {
                            // Server shutdown.
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown
        }
    }

    private async Task HandleClientAsync(int sessionId, TcpClient client, CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
        MapTelemetry.ConnectionsAccepted.Add(1);
        using var activity = MapTelemetry.ActivitySource.StartActivity("map.client.session", ActivityKind.Server);
        activity?.SetTag("net.peer.ip", endpoint?.Address.ToString());
        activity?.SetTag("net.peer.port", endpoint?.Port);
        MapLogger.Info($"[iRO MAP DEBUG] Client connected: {endpoint}");

        using (client)
        await using (var session = new MapClientSession(sessionId, client, _charConnector, _world))
        {
            _sessions[sessionId] = session;
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
            finally
            {
                _sessions.TryRemove(sessionId, out _);
            }
        }

        MapLogger.Info($"[iRO MAP DEBUG] Client disconnected: {endpoint}");
    }
}
