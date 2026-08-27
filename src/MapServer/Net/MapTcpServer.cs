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
                await ProcessOneMonsterTickAsync(_sessions.Values.ToArray(), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown
        }
    }

    // The exact per-tick body RunMonsterTickLoopAsync's own Task.Delay loop calls - extracted so a
    // test can drive ONE production tick deterministically (e.g. under a ControllableTimeProvider)
    // without needing to race a real 100ms Task.Delay via the private loop above, and WITHOUT any
    // test-only production API for session membership (see this project's own rejected
    // TeleportForTestAsync precedent - MapClientSession/MapTcpServer must never grow a method whose
    // sole purpose is test setup). `sessions` is an explicit parameter for exactly this reason: the
    // real caller above passes this instance's own live `_sessions.Values`, and a test passes
    // whatever real, already-authenticated MapClientSession instances it already constructed
    // itself - both go through the IDENTICAL fan-out algorithm below, unchanged.
    internal async Task ProcessOneMonsterTickAsync(IReadOnlyCollection<MapClientSession> sessions, CancellationToken cancellationToken)
    {
        // A respawned instance was removed from every session's own _visibleActorIds when it died
        // (existing vanish-on-death handling) and nothing else re-discovers it - idle-walk AI does
        // not run again for MinRandomWalkTimeMs+ (4000ms+) after a fresh respawn, so waiting for a
        // walk to accidentally re-trigger discovery would leave a respawned, stationary-so-far
        // Poring invisible to any session already looking at its spawn area for seconds. Reported
        // respawns are fanned out THIS SAME tick, reusing NotifyMonsterMovedAsync's own "not yet
        // visible, but now in range" discovery path (CellCrossed is the correct Kind here: the
        // instance is not walking - a fresh respawn's idle-walk timer has not fired yet - so this
        // always resolves to a plain 0x09FF stand entry, never a spurious 0x09FD).
        var respawned = _world.Monsters.ProcessDueRespawns();
        var changed = _world.MonsterRuntime.ProcessTick();
        var engagementResult = await _engagementProcessor.ProcessAsync(sessions, cancellationToken);

        // Every world-visible movement change this tick, from EITHER source (idle-walk AI or
        // combat engagement) - both flow through the exact same NotifyMonsterMovedAsync
        // visibility/wire-mapping path below, since from a session's own perspective a chase-driven
        // walk start is indistinguishable from an idle-walk one. Without including
        // engagementResult.MovementChanges here, a combat-driven chase/attack-interruption is
        // computed and logged by the processor but never actually reaches any client - the exact
        // live bug this fan-out exists to close (0x09FD/0x0088 "sent" only in the log, never on the
        // wire).
        if (changed.Count == 0 && respawned.Count == 0 && engagementResult.MovementChanges.Count == 0 && engagementResult.AttackActions.Count == 0) return;

        foreach (var session in sessions)
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

            foreach (var change in engagementResult.MovementChanges)
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

            // NotifyMonsterAttackOutcomeAsync owns its own visibility/victim rules internally
            // (AREA-visible 0x08C8 gated on _visibleActorIds, self-only SP_HP gated on
            // VictimAccountId+HpChanged, map-mismatch guard) - this loop only needs to call it once
            // per session per outcome, exactly like the movement fan-out above; no duplicate gating
            // logic belongs here.
            foreach (var action in engagementResult.AttackActions)
            {
                try
                {
                    await session.NotifyMonsterAttackOutcomeAsync(action, cancellationToken);
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
