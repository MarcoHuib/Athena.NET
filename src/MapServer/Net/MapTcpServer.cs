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
    // Item 7 of the Step 6 correctness-hardening pass: maps whose monster-feed reconciliation hit a
    // DETERMINISTIC invariant/configuration failure (see IsDeterministicInvariantFailure below) -
    // never retried by the ordinary per-tick loop, since a deterministic failure would simply
    // reproduce identically on every subsequent 100ms tick forever, hot-looping an error log without
    // ever making progress. Distinct from a transient World/transport failure (an unexpected but
    // NON-deterministic exception), which the ordinary per-map try/catch below still logs and lets
    // retry on the next tick exactly as before this item. A map only ever enters this set from
    // production code paths - never cleared automatically, since the underlying configuration/data
    // problem (e.g. an unknown generated MobId) requires an operator/config correction, not a retry.
    private readonly ConcurrentDictionary<string, string> _permanentlyFailedMaps = new(StringComparer.OrdinalIgnoreCase);
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
                try
                {
                    await ProcessOneMonsterTickAsync(_sessions.Values.ToArray(), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // Genuine shutdown - let the outer catch below handle it.
                }
                catch (Exception ex) when (IsDeterministicInvariantFailure(ex))
                {
                    // Item 7: an unexpected DETERMINISTIC invariant/configuration failure that
                    // escaped ProcessOneMonsterTickAsync's own per-map classification (e.g. thrown
                    // from the shared cadence executor, which is not scoped to one single map/try-
                    // catch) - this is not something a later tick can ever resolve by retrying, so
                    // propagate it out of the loop entirely rather than logging it every 100ms
                    // forever. RunAsync's own `await monsterTickLoop` in its `finally` block observes
                    // this fault - see that method's own doc comment for why this deliberately does
                    // NOT leave MapServer running indefinitely with a silently-dead monster-authority
                    // task.
                    MapLogger.Error($"[WORLD] Deterministic invariant/configuration failure in monster tick processing - the monster-authority loop cannot continue: {ex}");
                    throw;
                }
                catch (Exception ex)
                {
                    // An unexpected/transient exception from one tick's processing (e.g. a transient
                    // Orleans timeout/transport failure not already caught by the narrower IOException/
                    // OperationCanceledException guards inside PollAndReconcileMapAsync/
                    // InitializeMapSpawnsAsync/the per-map try/catch below) must never fault this
                    // entire background loop task permanently - the loop survives and naturally
                    // retries via its own next 100ms tick, nothing more elaborate (no blanket
                    // automatic retry/backoff is added here). Genuinely loud invariant/configuration
                    // failures (ContentMismatch/CallerFingerprintMismatch/SpawnMapMismatch) are still
                    // logged and left unretried by InitializeMapSpawnsAsync's own existing handling,
                    // which this catch does not change or suppress further.
                    MapLogger.Error($"[WORLD] Unhandled exception in monster tick processing - the loop will continue on its next tick: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown
        }
    }

    // Item 7 of the Step 6 correctness-hardening pass: distinguishes a DETERMINISTIC invariant/
    // configuration failure (one that would reproduce IDENTICALLY on every retry - e.g. an unknown
    // generated MobId reaching GeneratedMobRegistry.Get/WorldMonsterActorView, or any other
    // "impossible configuration state" the codebase asserts via a thrown exception rather than a
    // typed result) from an ordinary TRANSIENT World/transport failure (a network timeout, a
    // dropped Orleans connection/gateway, an Orleans-generated InvalidOperationException wrapping a
    // grain-call/activation problem, anything that might genuinely succeed on a LATER tick without
    // any configuration change). Deliberately narrow: InvalidOperationException is EXCLUDED
    // specifically because both a genuine local invariant violation (e.g.
    // WorldMonsterActorView/NotifyMonsterMovedAsync's own mismatched-actor/combat-state guard) AND
    // ordinary transient Orleans client-side failures can surface as that exact type, and this
    // codebase has no reliable way to tell those apart by type alone - misclassifying a transient
    // Orleans failure as permanent would incorrectly stop retrying a map that could have recovered
    // on its own. KeyNotFoundException is the concrete example this pass DOES classify as
    // deterministic: GeneratedMobRegistry.Get (used by WorldMonsterActorView and
    // MonsterFeedProjection's own GeneratedMobRegistryLookup) throws it specifically when a
    // referenced MobId has no corresponding generated static definition at all - a purely local,
    // in-process static-data lookup with no I/O involved, so it can never be a transient failure by
    // construction, and retrying the exact same poll can never fix it either.
    private static bool IsDeterministicInvariantFailure(Exception ex) =>
        ex is KeyNotFoundException;

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
        // Only sessions that have actually reached WorldVisible (authenticated AND registered with
        // a genuine World presence on a real map) are eligible to be grouped/polled by map id here -
        // a newly-accepted TCP session is inserted into MapTcpServer's own _sessions dictionary
        // BEFORE authentication/World registration completes (see HandleClientAsync), so its
        // CurrentMapName can be empty during that window. WorldMapId.Normalize rejects null/empty/
        // whitespace map ids, so grouping such a session together with real sessions (or polling for
        // map id "") is a bug this filter exists to prevent, never merely a cosmetic grouping choice.
        var eligibleSessions = sessions.Where(session => session.IsWorldMapEligible).ToArray();
        foreach (var mapGroup in eligibleSessions.GroupBy(session => session.CurrentMapName, StringComparer.OrdinalIgnoreCase))
        {
            if (_permanentlyFailedMaps.ContainsKey(mapGroup.Key)) continue; // Item 7: a deterministic invariant failure already logged for this map - never hot-loop retrying it.
            try
            {
                await PollAndReconcileMapAsync(mapGroup.Key, mapGroup.ToArray(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Genuine shutdown - propagate, never swallow.
            }
            catch (Exception ex) when (IsDeterministicInvariantFailure(ex))
            {
                // Item 7 of the Step 6 correctness-hardening pass: a DETERMINISTIC invariant/
                // configuration failure (e.g. an unknown generated MobId - see
                // IsDeterministicInvariantFailure's own doc comment) would reproduce IDENTICALLY on
                // every subsequent 100ms tick forever if treated like an ordinary transient failure -
                // that is a permanently-broken map silently hot-looping an error log, not resilience.
                // Fail loudly ONCE, and put this map into an explicit failed state requiring
                // operator/config correction rather than retrying it again.
                MapLogger.Error($"[WORLD] Deterministic invariant/configuration failure reconciling map '{mapGroup.Key}' - this map will NOT be retried until the underlying configuration/data problem is corrected (requires a MapServer restart to re-attempt): {ex}");
                _permanentlyFailedMaps[mapGroup.Key] = ex.Message;
            }
            catch (Exception ex)
            {
                // An unexpected but NON-deterministic (transient World/transport) exception
                // reconciling ONE map must never prevent every OTHER map in this SAME tick from
                // being processed - each mapGroup iteration is independent (separate
                // MonsterFeedProjection, separate cursor). Nothing about this map's in-flight
                // cursor/combat-state/session projection was left partially applied here:
                // PollAndReconcileMapAsync's own internal ordering only advances the cursor after
                // every earlier step succeeds (see MonsterFeedProjection's own doc comment), so a
                // failure here simply means this tick made no progress for this one map - the next
                // tick's own poll naturally retries from the same, unadvanced cursor.
                MapLogger.Error($"[WORLD] Unhandled exception reconciling map '{mapGroup.Key}' this tick - other maps still proceed, this map retries next tick: {ex}");
            }
        }

        var cadenceResult = await _cadenceExecutor.ProcessAsync(eligibleSessions, cancellationToken);
        foreach (var session in eligibleSessions)
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

            // Item 6 of the Step 6 correctness-hardening pass: retry any pending World life-state
            // update (e.g. a prior UpdatePresenceLifeStateAsync call that failed transiently right
            // after a real local Alive->Dead transition) on EVERY tick, not only immediately after
            // the transition that created it - a transient RPC failure must not leave a player
            // locally Dead while World indefinitely still reports IsAlive=true. A no-op call
            // (no RPC at all) when nothing is pending for this session.
            try
            {
                await session.TryReconcilePendingLifeStateAsync(cancellationToken);
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
    // client-visible monster projection. Delegates the full per-session diff (vanish-on-leave-AOI,
    // vanish-on-vanished/dead/old-incarnation/new-epoch actors, then rediscovery of everything
    // currently Alive and in-AOI) to MapClientSession.ReconcileMonsterVisibilityAsync - see that
    // method's own doc comment for the exact diff rules; MapTcpServer only owns session enumeration
    // (it has no socket/visibility state of its own to reconcile).
    private async Task ReconcileSessionsFullyAsync(MonsterFeedProjection projection, IReadOnlyCollection<MapClientSession> mapSessions, CancellationToken cancellationToken)
    {
        foreach (var session in mapSessions)
        {
            try
            {
                await session.ReconcileMonsterVisibilityAsync(projection, _world.CombatState, cancellationToken);
            }
            catch (IOException) { /* Client disconnected; HandleClientAsync's own cleanup removes it from _sessions. */ }
            catch (OperationCanceledException) { /* Server shutdown. */ }
        }
    }

    // Fans out one incremental feed entry to every session on this map. `Died` is fanned out to
    // EVERY session that currently has this actor visible (MapClientSession.NotifyMonsterDiedAsync
    // owns the per-session IsActorVisible gate and the actual vanish send) - the ATTACKER's own
    // session already sent its own death-vanish synchronously via its confirmed-local-kill path
    // (PerformDueRepeatAttackAsync's own outcome.KilledByThisHit branch, which runs on the
    // attacker's session's own repeat-attack loop, strictly before this SEPARATE MapTcpServer
    // monster-tick loop can ever observe/poll the resulting Died feed entry) and therefore no longer
    // has this actor marked visible by the time this runs, so NotifyMonsterDiedAsync's own
    // IsActorVisible guard naturally skips it without a duplicate send. Every OTHER session that
    // still had this monster visible (it never attacked it, or attacked a different one) has no
    // other path that would ever tell it this monster died, and would otherwise show a live,
    // undamaged monster forever. `Respawned` uses discovery (movementKind: null) so a session that
    // had marked the OLD incarnation's ActorId not-visible (removed on death) re-discovers the NEW
    // incarnation exactly like any other newly-visible actor. Every OTHER kind carrying a
    // MovementKind is projected via its own explicit WorldMonsterMovementKind (never inferred from
    // IsWalking - see WorldMonsterMovementKind's own doc comment).
    private async Task FanOutEntryAsync(WorldMonsterFeedEntry entry, WorldSimulationEpoch epoch, IReadOnlyCollection<MapClientSession> mapSessions, CancellationToken cancellationToken)
    {
        if (entry.Kind == WorldMonsterFeedEntryKind.Died)
        {
            foreach (var session in mapSessions)
            {
                try
                {
                    await session.NotifyMonsterDiedAsync(entry.ActorId, cancellationToken);
                }
                catch (IOException) { /* Client disconnected; HandleClientAsync's own cleanup removes it from _sessions. */ }
                catch (OperationCanceledException) { /* Server shutdown. */ }
            }
            return;
        }

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
