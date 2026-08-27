using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
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
    private readonly MonsterEngagementTickProcessor _engagementProcessor;
    private int _nextSessionId;

    public MapTcpServer(MapConfigStore configStore, CharServerConnector charConnector, MapServerWorld world, TimeProvider? timeProvider = null)
    {
        _configStore = configStore;
        _charConnector = charConnector;
        _world = world;
        var config = _configStore.Current;
        _listener = new TcpListener(config.BindIp, config.MapPort);
        _engagementProcessor = new MonsterEngagementTickProcessor(_world.Monsters, _world.Collision, _world.MovementPathProvider, timeProvider ?? TimeProvider.System);
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

                // A respawned instance was removed from every session's own _visibleActorIds when
                // it died (existing vanish-on-death handling) and nothing else re-discovers it -
                // idle-walk AI does not run again for MinRandomWalkTimeMs+ (4000ms+) after a fresh
                // respawn, so waiting for a walk to accidentally re-trigger discovery would leave a
                // respawned, stationary-so-far Poring invisible to any session already looking at
                // its spawn area for seconds. Reported respawns are fanned out THIS SAME tick,
                // reusing NotifyMonsterMovedAsync's own "not yet visible, but now in range" discovery
                // path (CellCrossed is the correct Kind here: the instance is not walking - a fresh
                // respawn's idle-walk timer has not fired yet - so this always resolves to a plain
                // 0x09FF stand entry, never a spurious 0x09FD).
                var respawned = _world.Monsters.ProcessDueRespawns();
                var changed = _world.MonsterRuntime.ProcessTick();

                await _engagementProcessor.ProcessAsync(_sessions.Values.ToArray(), cancellationToken);

                if (changed.Count == 0 && respawned.Count == 0) continue;

                foreach (var session in _sessions.Values)
                {
                    foreach (var change in changed)
                    {
                        try
                        {
                            await session.NotifyMonsterMovedAsync(change, cancellationToken);
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

                    foreach (var instance in respawned)
                    {
                        try
                        {
                            await session.NotifyMonsterMovedAsync(new MonsterMovementChange(instance, MonsterMovementChangeKind.CellCrossed), cancellationToken);
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
