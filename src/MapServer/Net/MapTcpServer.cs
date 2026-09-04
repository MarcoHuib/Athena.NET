using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Telemetry;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

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
    private readonly IWorldRuntime _worldRuntime;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<int, MapClientSession> _sessions = new();
    private readonly MonsterAttackCadenceExecutor _cadenceExecutor;
    private int _nextSessionId;

    public MapTcpServer(MapConfigStore configStore, CharServerConnector charConnector, MapServerWorld world, IWorldRuntime worldRuntime, TimeProvider? timeProvider = null)
    {
        _configStore = configStore;
        _charConnector = charConnector;
        _world = world;
        _worldRuntime = worldRuntime;
        var config = _configStore.Current;
        _listener = new TcpListener(config.BindIp, config.MapPort);
        _cadenceExecutor = new MonsterAttackCadenceExecutor(_world.MonsterProjections, _world.CombatState, _worldRuntime, timeProvider ?? TimeProvider.System);
    }

    // Focused tests which exercise the existing process-local simulation do not start an Orleans
    // cluster. Production startup always uses the overload above and requires IWorldRuntime.
    internal MapTcpServer(MapConfigStore configStore, CharServerConnector charConnector, MapServerWorld world, TimeProvider? timeProvider = null)
        : this(configStore, charConnector, world, new InMemoryTestWorldRuntime(), timeProvider)
    {
    }

    private sealed class InMemoryTestWorldRuntime : IWorldRuntime
    {
        private readonly Dictionary<uint, WorldPlayerPresence> _presences = [];
        private readonly Dictionary<Guid, WorldTransferResult> _transfers = [];
        private readonly Dictionary<uint, TestMovement> _movements = [];
        private readonly Lock _gate = new();

        public Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken) =>
            Task.FromResult(Register(mapId, presence));

        public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken) =>
            Task.FromResult(Unregister(mapId, characterId, presenceId));

        private WorldPresenceRegistration Register(string mapId, WorldPlayerPresence presence)
        {
            var normalized = MapName.NormalizeWorld(mapId).ToLowerInvariant();
            lock (_gate)
            {
                if (!_presences.TryGetValue(presence.CharacterId, out var existing))
                {
                    _presences.Add(presence.CharacterId, presence with { MapId = normalized });
                    return new("test-partition", normalized, WorldPresenceRegistrationStatus.Registered, Count(normalized));
                }
                if (existing.PresenceId != presence.PresenceId || existing.ActorId != presence.ActorId || !string.Equals(existing.MapId, normalized, StringComparison.OrdinalIgnoreCase))
                    return new("test-partition", normalized, WorldPresenceRegistrationStatus.Conflict, Count(normalized));
                _presences[presence.CharacterId] = presence with { MapId = normalized };
                return new("test-partition", normalized, WorldPresenceRegistrationStatus.AlreadyRegistered, Count(normalized));
            }
        }

        private WorldPresenceUnregistration Unregister(string mapId, uint characterId, Guid presenceId)
        {
            var normalized = MapName.NormalizeWorld(mapId).ToLowerInvariant();
            lock (_gate)
            {
                if (!_presences.TryGetValue(characterId, out var existing))
                    return new("test-partition", normalized, WorldPresenceUnregistrationStatus.AlreadyAbsent, Count(normalized));
                if (existing.PresenceId != presenceId)
                    return new("test-partition", normalized, WorldPresenceUnregistrationStatus.PresenceMismatch, Count(normalized));
                if (!string.Equals(existing.MapId, normalized, StringComparison.OrdinalIgnoreCase))
                    return new("test-partition", normalized, WorldPresenceUnregistrationStatus.MapMismatch, Count(normalized));
                _presences.Remove(characterId);
                return new("test-partition", normalized, WorldPresenceUnregistrationStatus.Removed, Count(normalized));
            }
        }

        private int Count(string mapId) => _presences.Values.Count(value => string.Equals(value.MapId, mapId, StringComparison.OrdinalIgnoreCase));

        public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_presences.TryGetValue(command.CharacterId, out var current)) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.NotFound, null));
                if (current.PresenceId != command.PresenceId) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.PresenceMismatch, current));
                if (!string.Equals(current.MapId, WorldMapId.Normalize(command.MapId), StringComparison.OrdinalIgnoreCase) || current.X != command.FromX || current.Y != command.FromY)
                    return Task.FromResult(new WorldMovementResult(WorldMovementStatus.SourceMismatch, current));
                var movementId = Guid.NewGuid();
                WorldPosition[] path = [new(command.FromX, command.FromY), new(command.DestinationX, command.DestinationY)];
                _movements[command.CharacterId] = new(movementId, path);
                return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Moved, current, path, movementId));
            }
        }

        public Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_movements.TryGetValue(command.CharacterId, out var movement) || movement.Id != command.MovementId)
                    return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Rejected, _presences.GetValueOrDefault(command.CharacterId)));
                if (command.DestinationIndex < 1 || command.DestinationIndex >= movement.Path.Length)
                    return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Rejected, _presences.GetValueOrDefault(command.CharacterId)));
                var path = movement.Path[..(command.DestinationIndex + 1)];
                _movements[command.CharacterId] = movement with { Path = path };
                return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Moved, _presences.GetValueOrDefault(command.CharacterId), path, command.MovementId));
            }
        }

        public Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_presences.TryGetValue(command.CharacterId, out var current))
                    return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.PresenceNotFound, null));
                if (current.PresenceId != command.PresenceId)
                    return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.PresenceMismatch, current));
                if (!_movements.TryGetValue(command.CharacterId, out var movement))
                    return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.AlreadyAbsent, current));
                if (movement.Id != command.MovementId)
                    return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.SourceMismatch, current));
                _movements.Remove(command.CharacterId);
                return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.Cancelled, current));
            }
        }

        public Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_presences.TryGetValue(command.CharacterId, out var current)) return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.NotFound, null));
                if (current.PresenceId != command.PresenceId) return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.PresenceMismatch, current));
                if (current.X != command.ExpectedX || current.Y != command.ExpectedY) return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.SourceMismatch, current));
                if (!_movements.TryGetValue(command.CharacterId, out var movement) || movement.Id != command.MovementId)
                    return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.StaleRoute, current));
                var currentIndex = Array.FindIndex(movement.Path, cell => cell.X == current.X && cell.Y == current.Y);
                if (currentIndex < 0 || currentIndex + 1 >= movement.Path.Length || movement.Path[currentIndex + 1] != new WorldPosition(command.NewX, command.NewY))
                    return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.Rejected, current));
                var advanced = current with { X = command.NewX, Y = command.NewY }; _presences[command.CharacterId] = advanced;
                if (currentIndex + 1 == movement.Path.Length - 1) _movements.Remove(command.CharacterId);
                return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.Advanced, advanced));
            }
        }

        public Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_transfers.TryGetValue(command.TransferId, out var replay)) return Task.FromResult(replay with { Status = WorldTransferStatus.AlreadyCompleted });
                if (!_presences.TryGetValue(command.CharacterId, out var current)) return Task.FromResult(new WorldTransferResult(WorldTransferStatus.NotFound, WorldTransferType.SamePartition, null));
                if (current.PresenceId != command.PresenceId || !string.Equals(current.MapId, WorldMapId.Normalize(command.SourceMapId), StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new WorldTransferResult(WorldTransferStatus.SourceMismatch, WorldTransferType.SamePartition, current));
                var moved = current with { MapId = WorldMapId.Normalize(command.DestinationMapId), X = command.DestinationX, Y = command.DestinationY };
                _presences[command.CharacterId] = moved;
                var result = new WorldTransferResult(WorldTransferStatus.Completed, WorldTransferType.SamePartition, moved);
                _transfers.Add(command.TransferId, result);
                return Task.FromResult(result);
            }
        }

        private sealed record TestMovement(Guid Id, WorldPosition[] Path);

        // Step 6: InMemoryTestWorldRuntime intentionally does NOT reimplement the monster-authority
        // RPCs' real grain semantics (spawn fingerprinting, sequenced feed/cursor/epoch, engagement
        // rules) - duplicating WorldMonsterMapSimulation's own logic here would be a second,
        // divergence-prone implementation of the exact authority this cutover exists to centralize
        // in one place. Any MapServer.Tests file that needs real monster-authority behavior spins
        // up a genuine Orleans TestCluster and uses OrleansWorldRuntime directly (see World.Tests'
        // own established TestClusterBuilder pattern) instead of this in-memory stand-in - this
        // class remains only for tests that exercise player-presence/movement/transfer behavior
        // with no monster involvement at all.
        public Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
        public Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
        public Task<WorldMonsterDeathResult> TryMarkMonsterDeadAsync(WorldMonsterLifeReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
        public Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
        public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
        public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
    }

    public int BoundPort { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        MapLogger.Status($"Map server listening on {_configStore.Current.BindIp}:{BoundPort}...");
        MapLogger.Status(
            $"WORLD: loaded {_world.Maps.EntityCount} world entities over {_world.Maps.MapCount} maps, {_world.Maps.StaticWarpCount} active warps, {_world.Maps.DynamicWarpActorCount} legacy dynamic/scripted warp actors, {_world.MonsterSpawns.Count} monster spawn declarations (World-authoritative simulation).");

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

    // The single shared driver for World-monster-feed polling/reconciliation and the local
    // attack-cadence executor. Every connected session observes the SAME World-authoritative
    // projection from this ONE loop, matching the requirement that monster movement seen by
    // different players originates from one source.
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
    // test can drive ONE production tick deterministically without needing to race a real 100ms
    // Task.Delay via the private loop above. `sessions` is an explicit parameter for exactly this
    // reason: the real caller above passes this instance's own live `_sessions.Values`, and a test
    // passes whatever real, already-authenticated MapClientSession instances it already constructed
    // itself - both go through the IDENTICAL algorithm below, unchanged.
    //
    // "Only poll maps which currently have active MapServer sessions" - maps are grouped from the
    // CURRENT session set every tick; a map with zero sessions this tick is simply skipped (its
    // MonsterFeedProjection, if one already exists, is left exactly as it was - see
    // MonsterFeedProjectionRegistry's own doc comment for why retaining state across a temporary
    // zero-session gap is explicitly correct, never destroyed).
    internal async Task ProcessOneMonsterTickAsync(IReadOnlyCollection<MapClientSession> sessions, CancellationToken cancellationToken)
    {
        foreach (var mapGroup in sessions.GroupBy(session => session.CurrentMapName, StringComparer.OrdinalIgnoreCase))
        {
            await PollAndReconcileMapAsync(mapGroup.Key, mapGroup.ToArray(), cancellationToken);
        }

        var cadenceResult = await _cadenceExecutor.ProcessAsync(sessions, cancellationToken);
        foreach (var session in sessions)
        {
            // NotifyMonsterAttackOutcomeAsync owns its own visibility/victim rules internally
            // (AREA-visible 0x08C8 gated on _visibleActorIds, self-only SP_HP gated on
            // VictimAccountId+HpChanged, map-mismatch guard) - this loop only needs to call it once
            // per session per outcome.
            foreach (var action in cadenceResult.AttackActions)
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

    // Polls World's monster feed for exactly ONE map and applies the BINDING bootstrap/resync/
    // incremental ordering (see MonsterFeedProjection's own doc comment for the full contract this
    // implements):
    //   SpawnInitializationRequired -> build+load the spawn batch, then re-poll for a fresh bootstrap
    //   ResyncRequired / first-ever poll (no cursor yet) -> full snapshot reconciliation, in order:
    //     1. projection  2. combat-state  3. every active session  4. THEN advance the cursor
    //   Ready with incremental Entries -> apply each entry in order, fan out any resulting movement
    //     packet per WorldMonsterMovementKind, THEN advance the cursor
    private async Task PollAndReconcileMapAsync(string mapId, IReadOnlyCollection<MapClientSession> mapSessions, CancellationToken cancellationToken)
    {
        var projection = _world.MonsterProjections.GetOrCreate(mapId);
        WorldMonsterFeedPage page;
        try
        {
            page = await _worldRuntime.PollMonsterFeedAsync(projection.Cursor, mapId, cancellationToken);
        }
        catch (IOException) { return; }
        catch (OperationCanceledException) { return; }

        if (page.Status == WorldMonsterFeedStatus.SpawnInitializationRequired)
        {
            await InitializeMapSpawnsAsync(mapId, cancellationToken);
            return; // The NEXT tick's poll picks up the fresh bootstrap this produced - see requirement 4's own "obtain a fresh atomic bootstrap after successful initialization" (a second poll here would double this tick's work for no benefit; the 100ms cadence makes the one-tick delay unobservable).
        }

        // A page carrying a full Snapshot is a full-reconciliation page - covers BOTH a genuine
        // ResyncRequired status AND the atomic first-ever bootstrap (cursor was null, Status is
        // Ready WITH a full Snapshot - see WorldMonsterMapSimulation.BuildPage's own "cursor is
        // null" branch). This must be detected via `page.Snapshot is not null`, NOT
        // `page.ResyncRequired` (`Status != Ready`) - a bootstrap page's own Status IS Ready, so
        // checking ResyncRequired here would skip reconciling the very first bootstrap entirely,
        // leaving the cursor never committed and the projection stuck with no epoch forever.
        if (page.Snapshot is { } snapshot)
        {
            projection.ApplySnapshot(snapshot, page.SimulationEpoch, _world.CombatState);
            await ReconcileSessionsFullyAsync(projection, mapSessions, cancellationToken);
            projection.CommitCursor(page.SimulationEpoch, page.AsOfSequence);
            return;
        }
        if (page.ResyncRequired) return; // ResyncRequired with a malformed/missing Snapshot - never partially reconcile; the next tick's own poll retries.

        if (page.Entries is not { Count: > 0 } entries) return;
        foreach (var entry in entries)
        {
            projection.ApplyEntry(entry, _world.CombatState, page.SimulationEpoch);
            await FanOutEntryAsync(entry, page.SimulationEpoch, mapSessions, cancellationToken);
        }
        projection.CommitCursor(page.SimulationEpoch, page.AsOfSequence);
    }

    // Requirement 4: builds the per-map WorldMonsterSpawnBatch from the existing generated spawn
    // declarations and calls LoadMonsterSpawnsAsync. Content/fingerprint/map mismatches are HARD
    // failures (logged, never silently swallowed into a retry-forever loop that could mask a real
    // configuration divergence) - Loaded/AlreadyLoaded are the only statuses that let this map
    // proceed to bootstrap on a later tick.
    private async Task InitializeMapSpawnsAsync(string mapId, CancellationToken cancellationToken)
    {
        var batch = WorldMonsterSpawnBatchBuilder.Build(mapId, _world.MonsterSpawns);
        WorldMonsterSpawnLoadResult result;
        try
        {
            result = await _worldRuntime.LoadMonsterSpawnsAsync(batch, cancellationToken);
        }
        catch (IOException) { return; }
        catch (OperationCanceledException) { return; }

        switch (result.Status)
        {
            case WorldMonsterSpawnLoadStatus.Loaded:
            case WorldMonsterSpawnLoadStatus.AlreadyLoaded:
                return; // Next tick's own poll obtains the fresh atomic bootstrap.
            case WorldMonsterSpawnLoadStatus.ContentMismatch:
            case WorldMonsterSpawnLoadStatus.CallerFingerprintMismatch:
            case WorldMonsterSpawnLoadStatus.SpawnMapMismatch:
                MapLogger.Error($"[WORLD] LoadMonsterSpawnsAsync for map '{mapId}' failed with {result.Status} - this map's monster spawns will NOT be loaded until this configuration divergence is resolved.");
                return;
        }
    }

    // Step 4 of the binding bootstrap/resync ordering: reconcile every active session's actual
    // client-visible monster projection. For each session: vanished/dead actors are handled by
    // simply no longer re-sending anything for an ActorId the fresh snapshot doesn't contain (this
    // project's own documented "no invented vanish packet" gap - see NotifyMonsterMovedAsync's own
    // doc comment; a full explicit vanish+rediscovery reconciliation is future work, not fabricated
    // here) - a session's own _visibleActorIds set naturally stops matching reality for a
    // no-longer-projected actor and will simply never receive further updates for it. Newly-visible/
    // currently-visible actors ARE explicitly (re-)projected here via NotifyMonsterMovedAsync's own
    // discovery path (movementKind: null - a bootstrap/resync is a state re-observation, never a
    // "movement just happened" event in its own right; discovery still triggers a stand/walk-entry
    // packet for a not-yet-visible actor, exactly like ordinary per-tick discovery does).
    private async Task ReconcileSessionsFullyAsync(MonsterFeedProjection projection, IReadOnlyCollection<MapClientSession> mapSessions, CancellationToken cancellationToken)
    {
        foreach (var session in mapSessions)
        {
            foreach (var instance in projection.AllInstances)
            {
                if (instance.Lifecycle != WorldMonsterLifecycleState.Alive) continue;
                if (!_world.CombatState.TryGet(new MonsterCombatKey(projection.MapId, projection.CurrentEpoch!.Value, instance.ActorId, instance.IncarnationId), out var combat)) continue;
                try
                {
                    await session.NotifyMonsterMovedAsync(new WorldMonsterActorView(instance), movementKind: null, combat, cancellationToken);
                }
                catch (IOException) { /* Client disconnected; HandleClientAsync's own cleanup removes it from _sessions. */ }
                catch (OperationCanceledException) { /* Server shutdown. */ }
            }
        }
    }

    // Fans out one incremental feed entry to every session on this map. `Died` sends nothing new
    // (a session's existing 0x0080 vanish-on-death handling, driven by the EXISTING attack-outcome/
    // kill path, already covers the visible removal - Died itself carries no NEW wire behavior here
    // beyond what the projection update already recorded) - `Respawned` uses discovery (movementKind:
    // null) so a session that had marked the OLD incarnation's ActorId not-visible (removed on death)
    // re-discovers the NEW incarnation exactly like any other newly-visible actor. Every OTHER kind
    // carrying a MovementKind is projected via its own explicit WorldMonsterMovementKind (never
    // inferred from IsWalking - see WorldMonsterMovementKind's own doc comment).
    private async Task FanOutEntryAsync(WorldMonsterFeedEntry entry, WorldSimulationEpoch epoch, IReadOnlyCollection<MapClientSession> mapSessions, CancellationToken cancellationToken)
    {
        if (entry.Kind == WorldMonsterFeedEntryKind.Died) return;

        var actor = new WorldMonsterActorView(entry.Instance);
        var movementKind = entry.Kind == WorldMonsterFeedEntryKind.Respawned ? null : entry.MovementKind;
        if (!_world.CombatState.TryGet(new MonsterCombatKey(entry.Instance.MapId, epoch, entry.ActorId, entry.IncarnationId), out var combat)) return;
        foreach (var session in mapSessions)
        {
            try
            {
                await session.NotifyMonsterMovedAsync(actor, movementKind, combat, cancellationToken);
            }
            catch (IOException) { /* Client disconnected; HandleClientAsync's own cleanup removes it from _sessions. */ }
            catch (OperationCanceledException) { /* Server shutdown. */ }
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
        await using (var session = new MapClientSession(sessionId, client, _charConnector, _world, _worldRuntime))
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
                MapLogger.Warning($"Client session error: {ex}");
            }
            finally
            {
                _sessions.TryRemove(sessionId, out _);
            }
        }

        MapLogger.Info($"[iRO MAP DEBUG] Client disconnected: {endpoint}");
    }
}
