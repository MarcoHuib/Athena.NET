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
    // Cross-session (this gateway process only) ordering primitive for a lethal player attack action
    // vs this SAME process's own Died feed dispatch - see LethalAttackProjectionGate's own doc comment.
    private readonly LethalAttackProjectionGate _lethalAttackGate = new();
    // Test-only exposure so a fixture that constructs MapClientSession directly (not via HandleClientAsync's
    // own real accept path) can still wire the SAME gate instance in. Never used by any production code path.
    internal LethalAttackProjectionGate LethalAttackGateForTest => _lethalAttackGate;
    // Defensive upper bound on FanOutEntryAsync's own wait below: normal release happens the instant
    // the attacking session's local action fan-out completes (microseconds to low milliseconds), never
    // this long in practice. This bound exists only so a stuck/disconnected attacker session can never
    // block THIS ONE life's Died dispatch forever - it is a fail-safe, not the ordering mechanism.
    private static readonly TimeSpan LethalAttackGateWaitTimeout = TimeSpan.FromSeconds(5);
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
        // Reconstructs this process's contribution to CharServer's own aggregate the instant a
        // (re)connection to CharServer succeeds - see CharServerConnector.CurrentUserCountProvider's
        // own doc comment for why this is necessary on top of the per-event reports below.
        _charConnector.CurrentUserCountProvider = () => (uint)_sessions.Values.Count(session => session.IsAuthenticated);
    }

    // Recomputes this process's own currently-authenticated player count and reports it to
    // CharServer (which aggregates across every registered MapServer and forwards the total to
    // LoginServer via LcUserCount - see MapServerRegistry.TotalUsers/LoginServerConnector.
    // TrySendUserCount on the CharServer side). Called exactly twice per session lifecycle: once when
    // MapClientSession's own authentication succeeds (_onAuthenticated, wired at construction below),
    // and once - unconditionally, whether or not the session ever authenticated - right after it is
    // removed from _sessions on disconnect, so the count can only ever go down for a session that
    // never actually incremented it, never negative.
    private void ReportAuthenticatedUserCount() => _ = _charConnector.TrySendUserCountAsync((uint)_sessions.Values.Count(session => session.IsAuthenticated));

    // Test-only read of the exact count ReportAuthenticatedUserCount would report - lets a test
    // assert the counting logic itself (authenticated-only, disconnect-decrements, never negative)
    // without needing a live CharServerConnector connection. Never called from any production path.
    internal int AuthenticatedSessionCountForTest => _sessions.Values.Count(session => session.IsAuthenticated);

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
        public Task<WorldMonsterDamageResult> ApplyMonsterDamageAsync(WorldMonsterDamageCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
        public Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
        public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
        public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken) =>
            throw new NotSupportedException("InMemoryTestWorldRuntime does not implement monster-authority RPCs - use a real Orleans TestCluster with OrleansWorldRuntime for tests that need monster behavior.");
    }

    public int BoundPort { get; private set; }

    // Item 4 of the Step 6 final correctness pass: the accept loop and the monster-authority loop
    // now supervise EACH OTHER via a linked cancellation source, instead of the accept loop running
    // to completion independently and only observing the monster loop's own outcome afterward in a
    // `finally` block. The earlier shape left a real gap: if `monsterTickLoop` faulted from a
    // deterministic invariant while the TCP listener was healthy, `RunAsync` stayed blocked inside
    // `AcceptTcpClientAsync` and kept accepting new players indefinitely, with the fatal failure only
    // ever observed once the accept loop happened to end for some OTHER, unrelated reason - directly
    // violating "MapServer must never unknowingly continue indefinitely with a permanently-dead
    // monster-authority task". Task.WhenAny below reacts to whichever sibling finishes FIRST:
    //   - the monster loop faults (a deterministic invariant escaped RunMonsterTickLoopAsync's own
    //     classification) -> cancel the linked token (stops accepting new clients) and PROPAGATE the
    //     fatal exception out of RunAsync itself, so the caller/process supervisor observes it
    //     promptly instead of MapServer silently limping along with a dead monster-authority task.
    //   - genuine external cancellation (the caller's own `cancellationToken`) -> both sibling loops
    //     observe it via the SAME linked token and exit normally; RunAsync awaits both cleanly.
    //   - the accept loop itself ends first (e.g. the listener socket faults) -> cancel the linked
    //     token so the monster loop also winds down, then await it.
    // Transient per-tick/per-map failures never reach this level at all - RunMonsterTickLoopAsync's
    // own classification (IsDeterministicInvariantFailure for the known unknown-MobId-shaped case,
    // WorldRpcFailureClassifier.IsTransientWorldRpcFailure for genuine Orleans transport/gateway/
    // timeout failures - item 3 of the Step 6 final correctness pass) already keeps those from
    // faulting the monster loop's task in the first place; only an exception that is NEITHER of
    // those two verified categories (a genuinely unexpected/unclassified failure, deliberately
    // treated the same as a deterministic one - see that loop's own final `catch` doc comment)
    // propagates this far.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        MapLogger.Status($"Map server listening on {_configStore.Current.BindIp}:{BoundPort}...");
        MapLogger.Status(
            $"WORLD: loaded {_world.Maps.EntityCount} world entities over {_world.Maps.MapCount} maps, {_world.Maps.StaticWarpCount} active warps, {_world.Maps.DynamicWarpActorCount} legacy dynamic/scripted warp actors, {_world.MonsterSpawns.Count} monster spawn declarations (World-authoritative simulation).");

        using var supervision = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monsterTickLoop = RunMonsterTickLoopAsync(supervision.Token);
        var acceptLoop = RunAcceptLoopAsync(supervision.Token);

        var firstCompleted = await Task.WhenAny(monsterTickLoop, acceptLoop);
        // Whichever sibling finished first (normally or by fault), cancel the OTHER one via the
        // shared linked token so it winds down promptly rather than continuing to run against a
        // MapServer that is already shutting down/already fatally broken.
        await supervision.CancelAsync();
        _listener.Stop();

        if (firstCompleted == monsterTickLoop && monsterTickLoop.IsFaulted)
        {
            // The monster-authority loop faulted from an already-classified deterministic invariant
            // failure (RunMonsterTickLoopAsync's own IsDeterministicInvariantFailure catch already
            // filtered out ordinary transient failures before this could ever happen) - propagate it
            // out of RunAsync itself. Still await the accept loop first so its own orderly shutdown
            // (draining AcceptTcpClientAsync's cancellation) completes before this method returns/throws.
            try { await acceptLoop; } catch (OperationCanceledException) { }
            await monsterTickLoop; // Rethrows the original fault (never re-wrapped) via awaiting the already-completed, faulted task.
            return;
        }

        // Normal paths: external cancellation, or the accept loop ending on its own (e.g. listener
        // fault) - await both siblings so any exception either one legitimately still holds surfaces
        // through Task.WhenAll rather than being silently dropped, while ordinary cancellation is
        // swallowed here exactly like the pre-existing behavior.
        try
        {
            await Task.WhenAll(acceptLoop, monsterTickLoop);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
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
            // Shutdown (either the caller's own cancellationToken, or the linked supervision token
            // being cancelled because the sibling monster-authority loop faulted - see RunAsync's
            // own doc comment).
        }
        catch (ObjectDisposedException)
        {
            // The listener was already stopped (RunAsync's own supervision logic calls
            // _listener.Stop() as soon as either sibling completes) - a benign race with
            // AcceptTcpClientAsync observing the disposed socket before it observes the
            // cancellation token.
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
                catch (Exception ex) when (WorldRpcFailureClassifier.IsTransientWorldRpcFailure(ex))
                {
                    // Item 3 of the Step 6 final correctness pass: ONLY the shared, narrow,
                    // explicitly-verified transient-World-RPC-failure classification (the SAME one
                    // MapClientSession's own repeat-attack loop uses) is retried here - a genuine
                    // Orleans timeout/transport/gateway failure not already caught by the narrower
                    // IOException/OperationCanceledException guards inside PollAndReconcileMapAsync/
                    // InitializeMapSpawnsAsync/the per-map try/catch below. This must never fault this
                    // entire background loop task permanently - the loop survives and naturally
                    // retries via its own next 100ms tick, nothing more elaborate (no blanket
                    // automatic retry/backoff is added here). Genuinely loud invariant/configuration
                    // failures (ContentMismatch/CallerFingerprintMismatch/SpawnMapMismatch) are still
                    // logged and left unretried by InitializeMapSpawnsAsync's own existing handling
                    // (marked permanently failed), which this catch does not change or suppress
                    // further.
                    MapLogger.Error($"[WORLD] Transient World RPC failure in monster tick processing - the loop will continue on its next tick: {ex}");
                }
                catch (Exception ex)
                {
                    // Item 3's own correction: the earlier broad, UNCONDITIONAL `catch (Exception ex)`
                    // that used to sit here logged-and-swallowed EVERY exception type not already
                    // classified as deterministic/transient above, including a genuine local
                    // programming/invariant defect (NullReferenceException, ArgumentException, an
                    // unexpected InvalidOperationException, an unrelated CharServer/persistence
                    // exception surfacing through this same call path) - silently logging it every
                    // single 100ms tick forever instead of ever surfacing it. Anything that reaches
                    // THIS catch now (not a KeyNotFoundException-shaped deterministic invariant
                    // failure per IsDeterministicInvariantFailure above, not a shutdown cancellation,
                    // not a verified transient World RPC failure per WorldRpcFailureClassifier) is, by
                    // elimination, an unexpected/unclassified failure that must be treated exactly
                    // like a deterministic one: propagate it out of the loop entirely so RunAsync's
                    // own supervision (see that method's own doc comment) observes and fails on it
                    // promptly, rather than hiding it behind an infinite retry loop.
                    MapLogger.Error($"[WORLD] Deterministic invariant/programming failure in monster tick processing - the monster-authority loop cannot continue: {ex}");
                    throw;
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
    // deterministic: GeneratedMobRegistry.Get (used by WorldMonsterActorView) throws it
    // specifically when a referenced MobId has no corresponding generated static definition at all - a purely local,
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
        // DEBUG-LOG-ONLY tick timing (see MonsterTickTiming) - never read by any decision.
        var tickStartedAt = Stopwatch.GetTimestamp();
        var timing = new MonsterTickTiming();
        foreach (var mapGroup in eligibleSessions.GroupBy(session => session.CurrentMapName, StringComparer.OrdinalIgnoreCase))
        {
            if (_permanentlyFailedMaps.ContainsKey(mapGroup.Key)) continue; // Item 7: a deterministic invariant failure already logged for this map - never hot-loop retrying it.
            timing.MapCount++;
            var mapPollStartedAt = Stopwatch.GetTimestamp();
            try
            {
                await PollAndReconcileMapAsync(mapGroup.Key, mapGroup.ToArray(), timing, cancellationToken);
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
            catch (Exception ex) when (WorldRpcFailureClassifier.IsTransientWorldRpcFailure(ex))
            {
                // Item 3 of the Step 6 final correctness pass: ONLY the shared, narrow, explicitly-
                // verified transient-World-RPC-failure classification is retried here - a genuine
                // Orleans timeout/transport/gateway failure reconciling ONE map must never prevent
                // every OTHER map in this SAME tick from being processed (each mapGroup iteration is
                // independent - separate MonsterFeedProjection, separate cursor). Nothing about this
                // map's in-flight cursor/combat-state/session projection was left partially applied
                // here: PollAndReconcileMapAsync's own internal ordering only advances the cursor
                // after every earlier step succeeds (see MonsterFeedProjection's own doc comment), so
                // a failure here simply means this tick made no progress for this one map - the next
                // tick's own poll naturally retries from the same, unadvanced cursor.
                MapLogger.Error($"[WORLD] Transient World RPC failure reconciling map '{mapGroup.Key}' - other maps still proceed, this map retries next tick: {ex}");
            }
            timing.PollAndReconcileTicks += Stopwatch.GetTimestamp() - mapPollStartedAt;
            // Item 3's own correction: the earlier broad, UNCONDITIONAL `catch (Exception ex)` that
            // used to sit here (with no `when` filter) would have caught EVERY exception type not
            // already classified as deterministic/transient above - including a genuine local
            // programming/invariant defect - and silently logged-and-swallowed it EVERY 100ms tick
            // forever for this map, never surfacing it. Anything reaching this point now is, by
            // elimination, unexpected/unclassified and must propagate exactly like a deterministic
            // failure would - this per-map try/catch does not swallow it; it escapes to
            // RunMonsterTickLoopAsync's own outer classification, which (per that method's own
            // identical correction) also propagates it to RunAsync's supervision rather than hiding
            // it behind an infinite per-map retry.
        }

        var cadenceStartedAt = Stopwatch.GetTimestamp();
        var cadenceResult = await _cadenceExecutor.ProcessAsync(eligibleSessions, cancellationToken);
        timing.CadenceTicks = Stopwatch.GetTimestamp() - cadenceStartedAt;
        timing.AttackActions = cadenceResult.AttackActions.Count;
        timing.SessionCount = eligibleSessions.Length;
        foreach (var session in eligibleSessions)
        {
            // NotifyMonsterAttackOutcomeAsync owns its own visibility/victim rules internally
            // (AREA-visible 0x08C8 gated on _visibleActorIds, self-only SP_HP gated on
            // VictimAccountId+HpChanged, map-mismatch guard) - this loop only needs to call it once
            // per session per outcome.
            foreach (var action in cadenceResult.AttackActions)
            {
                var outcomeStartedAt = Stopwatch.GetTimestamp();
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
                timing.OutcomeFanOutTicks += Stopwatch.GetTimestamp() - outcomeStartedAt;
            }

            // Item 6 of the Step 6 correctness-hardening pass: retry any pending World life-state
            // update (e.g. a prior UpdatePresenceLifeStateAsync call that failed transiently right
            // after a real local Alive->Dead transition) on EVERY tick, not only immediately after
            // the transition that created it - a transient RPC failure must not leave a player
            // locally Dead while World indefinitely still reports IsAlive=true. A no-op call
            // (no RPC at all) when nothing is pending for this session.
            var pendingLifeStartedAt = Stopwatch.GetTimestamp();
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
            timing.PendingLifeTicks += Stopwatch.GetTimestamp() - pendingLifeStartedAt;
        }

        LogTickTimingSummary(timing, Stopwatch.GetTimestamp() - tickStartedAt);
    }

    // DEBUG-LOG-ONLY per-tick timing (no behavior, not guarded by the verbose toggle): raw
    // Stopwatch ticks accumulated by ProcessOneMonsterTickAsync/PollAndReconcileMapAsync and turned
    // into ONE summary line only for a tick that is slow (>= TickSummaryThresholdMs) or that carried a
    // monster attack. Answers "where did start-to-start > 100 ms go" without a telemetry framework.
    private sealed class MonsterTickTiming
    {
        public long PollAndReconcileTicks;
        public long PollRpcTicks;
        public long CadenceTicks;
        public long OutcomeFanOutTicks;
        public long PendingLifeTicks;
        public int Entries;
        public int AttackActions;
        public int MapCount;
        public int SessionCount;
    }

    private const double TickSummaryThresholdMs = 15;

    private static double TicksToMs(long ticks) => Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds;

    private static void LogTickTimingSummary(MonsterTickTiming timing, long totalTicks)
    {
        var totalMs = TicksToMs(totalTicks);
        if (totalMs < TickSummaryThresholdMs && timing.AttackActions == 0) return;

        static string F(double value) => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        var pollReconcileMs = TicksToMs(timing.PollAndReconcileTicks);
        var pollRpcMs = TicksToMs(timing.PollRpcTicks);
        MapLogger.Info(
            $"[iRO MAP DEBUG] Monster tick timing totalMs={F(totalMs)} pollAndReconcileMs={F(pollReconcileMs)} pollRpcMs={F(pollRpcMs)} " +
            $"applyAndFanOutMs={F(Math.Max(0, pollReconcileMs - pollRpcMs))} cadenceMs={F(TicksToMs(timing.CadenceTicks))} " +
            $"outcomeFanOutMs={F(TicksToMs(timing.OutcomeFanOutTicks))} pendingLifeMs={F(TicksToMs(timing.PendingLifeTicks))} " +
            $"entries={timing.Entries} attackActions={timing.AttackActions} maps={timing.MapCount} sessions={timing.SessionCount} " +
            $"t={F(Stopwatch.GetElapsedTime(0).TotalMilliseconds)}ms");
    }

    // Polls World's monster feed for exactly ONE map and applies the BINDING bootstrap/resync/
    // incremental ordering (see MonsterFeedProjection's own doc comment for the full contract this
    // implements):
    //   SpawnInitializationRequired -> build+load the spawn batch, then re-poll for a fresh bootstrap
    //   ResyncRequired / first-ever poll (no cursor yet) -> full snapshot reconciliation, in order:
    //     1. projection  2. combat-state  3. every active session  4. THEN advance the cursor
    //   Ready with incremental Entries -> apply each entry in order, fan out any resulting movement
    //     packet per WorldMonsterMovementKind, THEN advance the cursor
    private async Task PollAndReconcileMapAsync(string mapId, IReadOnlyCollection<MapClientSession> mapSessions, MonsterTickTiming timing, CancellationToken cancellationToken)
    {
        var projection = _world.MonsterProjections.GetOrCreate(mapId);
        WorldMonsterFeedPage page;
        var pollStartedAt = Stopwatch.GetTimestamp();
        var cursorBeforePoll = projection.Cursor;
        try
        {
            page = await _worldRuntime.PollMonsterFeedAsync(cursorBeforePoll, mapId, cancellationToken);
        }
        catch (IOException) { timing.PollRpcTicks += Stopwatch.GetTimestamp() - pollStartedAt; return; }
        catch (OperationCanceledException) { timing.PollRpcTicks += Stopwatch.GetTimestamp() - pollStartedAt; return; }
        timing.PollRpcTicks += Stopwatch.GetTimestamp() - pollStartedAt;
        timing.Entries += page.Entries?.Count ?? 0;
        LogFeedPollDiagnostics(mapId, page, cursorBeforePoll, pollStartedAt);

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

    // DEBUG-LOG-ONLY movement-lag diagnostics (no behavior): one line per poll that carried
    // incremental entries or a snapshot, or whose RPC was slow. Lets a live capture separate real
    // Orleans/feed latency from client-interpolation effects: rpcMs is the PollMonsterFeedAsync
    // round trip, sincePrevPollMs is the real poll cadence for this map (Task.Delay(100ms) +
    // processing, NOT a fixed-rate timer), cursorLag is how many World sequences this poll had to
    // catch up on (asOfSequence - cursor.Sequence; ~0 means the feed is being consumed promptly).
    private readonly ConcurrentDictionary<string, long> _debugLastPollTimestampByMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly double DebugSlowPollThresholdMs = 25;

    private void LogFeedPollDiagnostics(string mapId, WorldMonsterFeedPage page, WorldMonsterFeedCursor? cursorBeforePoll, long pollStartedAt)
    {
        if (!MonsterDebugLog.Verbose) return;
        var finishedAt = Stopwatch.GetTimestamp();
        var rpcMs = Stopwatch.GetElapsedTime(pollStartedAt, finishedAt).TotalMilliseconds;
        var previous = _debugLastPollTimestampByMap.TryGetValue(mapId, out var previousStart) ? previousStart : (long?)null;
        _debugLastPollTimestampByMap[mapId] = pollStartedAt;

        var entryCount = page.Entries?.Count ?? 0;
        var hasSnapshot = page.Snapshot is { Count: > 0 };
        if (entryCount == 0 && !hasSnapshot && rpcMs < DebugSlowPollThresholdMs) return;

        var sincePrevMs = previous is { } prev ? Stopwatch.GetElapsedTime(prev, pollStartedAt).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "none";
        var firstSeq = entryCount > 0 ? page.Entries![0].Sequence : (long?)null;
        var lastSeq = entryCount > 0 ? page.Entries![entryCount - 1].Sequence : (long?)null;
        var cursorLag = cursorBeforePoll is { } cursor ? (page.AsOfSequence - cursor.Sequence).ToString() : "bootstrap";
        MapLogger.Info(
            $"[iRO MAP DEBUG] Feed poll map={mapId} status={page.Status} rpcMs={rpcMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} sincePrevPollMs={sincePrevMs} " +
            $"entries={entryCount} seq=[{firstSeq?.ToString() ?? "-"}..{lastSeq?.ToString() ?? "-"}] asOf={page.AsOfSequence} cursorLag={cursorLag} snapshot={(hasSnapshot ? page.Snapshot!.Count.ToString() : "no")} " +
            $"t={Stopwatch.GetElapsedTime(0).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}ms");
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
                // Item 3 of the Step 6 final correctness pass: these are PERMANENT configuration
                // divergences, not transient failures - without marking this map failed here, the
                // NEXT 100ms tick's own poll would report SpawnInitializationRequired again (World
                // never accepted the load), re-enter this exact method, hit the exact same mismatch,
                // and log an identical error forever. Reuse the existing _permanentlyFailedMaps
                // mechanism (already checked before every per-map poll in ProcessOneMonsterTickAsync)
                // so this map is never retried again until an operator restarts MapServer with
                // corrected spawn configuration - logged exactly ONCE, here.
                MapLogger.Error($"[WORLD] LoadMonsterSpawnsAsync for map '{mapId}' failed with {result.Status} - this map's monster spawns will NOT be loaded until this configuration divergence is resolved; the map is now permanently failed and will not be retried until MapServer restarts with corrected configuration.");
                _permanentlyFailedMaps[mapId] = $"LoadMonsterSpawnsAsync {result.Status}";
                return;
        }
    }

    // Step 4 of the binding bootstrap/resync ordering: reconcile every active session's actual
    // client-visible monster projection. Delegates the full per-session diff (vanish-on-leave-AOI,
    // vanish-on-vanished/dead/old-incarnation/new-epoch actors, then rediscovery of everything
    // currently Alive and in-AOI) to MapClientSession.ReconcileMonsterVisibilityAsync - see that
    // method's own doc comment for the exact diff rules; MapTcpServer only owns session enumeration
    // (it has no socket/visibility state of its own to reconcile). Step 7 substep 5: no longer passes
    // _world.CombatState - HP for client projection now comes exclusively from the World-authoritative
    // WorldMonsterInstance already carried by `projection`.
    private async Task ReconcileSessionsFullyAsync(MonsterFeedProjection projection, IReadOnlyCollection<MapClientSession> mapSessions, CancellationToken cancellationToken)
    {
        foreach (var session in mapSessions)
        {
            try
            {
                await session.ReconcileMonsterVisibilityAsync(projection, cancellationToken);
            }
            catch (IOException) { /* Client disconnected; HandleClientAsync's own cleanup removes it from _sessions. */ }
            catch (OperationCanceledException) { /* Server shutdown. */ }
        }
    }

    // Local, same-gateway-process fan-out for a player's already-World-confirmed attack action (see
    // PlayerAttackActionOutcome's own doc comment for why MapClientSession never iterates sibling
    // sessions itself - this is the ONE place that does, mirroring FanOutEntryAsync's own role for
    // the monster feed). Every CURRENTLY connected session - the attacker's own session included -
    // gets exactly one call; each session's own NotifyPlayerAttackActionAsync owns its map/visibility
    // gate and decides independently whether the action is actually wire-visible to it.
    //
    // ARCHITECTURE NOTE: this fan-out only reaches sessions connected to THIS MapServer process. Two
    // players connected through two different MapServer gateway replicas do not yet share player
    // combat-action visibility this way - that requires a World/Orleans player/combat event feed
    // (the same migration PlayerVisibilityCoordinator/PlayerPresenceRegistry already need for
    // cross-replica player visibility in general - see ai/map-server.md), which is out of scope here.
    internal async Task FanOutPlayerAttackActionAsync(PlayerAttackActionOutcome action, CancellationToken cancellationToken)
    {
        foreach (var session in _sessions.Values)
        {
            try
            {
                await session.NotifyPlayerAttackActionAsync(action, cancellationToken);
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

    // Fans out one incremental feed entry to every session on this map. `Died` is fanned out to
    // EVERY session on the map, passing the EXACT life identity (item 1 of the Step 6 final
    // correctness pass corrected this from ActorId-only) - MapClientSession.NotifyMonsterDiedAsync
    // owns the per-session visibility gate AND the arbitration against its own possibly-in-flight
    // local lethal projection for that SAME exact life (see LethalDeathProjectionArbiter's own doc
    // comment for the race this closes: World's death confirmation now happens BEFORE the local
    // lethal projection completes, so this SEPARATE tick loop's own Died observation can genuinely
    // race the attacker's own session finishing its wire/reward sequence - the old assumption that
    // the attacker's session "necessarily" already vanished itself before this loop could ever
    // observe Died no longer holds). Every OTHER session that still had this monster visible (it
    // never attacked it, or attacked a different one) has no other path that would ever tell it this
    // monster died, and would otherwise show a live, undamaged monster forever. `Respawned` uses
    // discovery (movementKind: null) so a session that had marked the OLD incarnation's ActorId
    // not-visible (removed on death) re-discovers the NEW incarnation exactly like any other
    // newly-visible actor. Every OTHER kind carrying a MovementKind is projected via its own explicit
    // WorldMonsterMovementKind (never inferred from IsWalking - see that type's own doc comment).
    internal async Task FanOutEntryAsync(WorldMonsterFeedEntry entry, WorldSimulationEpoch epoch, IReadOnlyCollection<MapClientSession> mapSessions, CancellationToken cancellationToken)
    {
        if (entry.Kind == WorldMonsterFeedEntryKind.Died)
        {
            var life = new WorldMonsterLifeReference(entry.Instance.MapId, epoch, entry.ActorId, entry.IncarnationId);
            // Live multiplayer regression fix: if a local player-attack lethal hit for this EXACT life
            // is currently being projected (LethalAttackProjectionGate.Enter was called before the
            // World RPC that produced this very Died entry was even dispatched - see
            // MapClientSession.EnterLethalInFlight), wait for that session to finish fanning its own
            // final 0x08C8 out to every local session BEFORE this Died vanish reaches anyone - so no
            // observer can ever see the vanish without having first seen the killing action. Bounded
            // (LethalAttackGateWaitTimeout) so a stuck/disconnected attacker can never block this one
            // life's Died dispatch forever; a life with no open gate returns immediately, so this adds
            // no latency to the overwhelmingly common non-racing case, and never touches any OTHER life.
            try
            {
                await _lethalAttackGate.WaitAsync(life).WaitAsync(LethalAttackGateWaitTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                MapLogger.Warning($"[iRO MAP DEBUG] LethalAttackProjectionGate wait timed out mobActorId={life.ActorId} map={life.MapId} - proceeding with Died fan-out anyway.");
            }
            foreach (var session in mapSessions)
            {
                try
                {
                    await session.NotifyMonsterDiedAsync(life, cancellationToken);
                }
                catch (IOException) { /* Client disconnected; HandleClientAsync's own cleanup removes it from _sessions. */ }
                catch (OperationCanceledException) { /* Server shutdown. */ }
            }
            return;
        }

        // Step 7 substep 7 (§14.5): a Respawned entry means World just minted a NEW incarnation for
        // this ActorId - before this fan-out's own ordinary discovery projection (below) runs for
        // that new life, tell every session's own LethalDeathProjectionArbiter to forget any stale
        // `_alreadyProjected` marker it may still be holding for an OLD incarnation of this same
        // ActorId (see NotifyMonsterRespawnedAsync's own doc comment for why this cleanup cannot be
        // deferred indefinitely). Deliberately does NOT return after this - the ordinary discovery
        // path immediately below is what actually re-introduces the new incarnation to each session,
        // exactly like any other newly-visible actor.
        if (entry.Kind == WorldMonsterFeedEntryKind.Respawned)
        {
            // Synchronous, pure in-memory cleanup (see NotifyMonsterRespawnedAsync's own doc
            // comment) - no I/O, so unlike every other per-session call in this method, no
            // IOException/OperationCanceledException guard is needed here.
            var newLife = new WorldMonsterLifeReference(entry.Instance.MapId, epoch, entry.ActorId, entry.IncarnationId);
            foreach (var session in mapSessions)
                session.NotifyMonsterRespawnedAsync(newLife);
        }

        // Step 7 substep 5: entry.Instance (World-authoritative, includes CurrentHp/MaxHp) is the
        // sole HP source for this fan-out - the prior _world.CombatState.TryGet lookup here was a
        // redundant second read purely to obtain HP that already sits in entry.Instance. A missing
        // transitional local combat-state entry must no longer suppress this projection.
        var actor = new WorldMonsterActorView(entry.Instance);
        var movementKind = entry.Kind == WorldMonsterFeedEntryKind.Respawned ? null : entry.MovementKind;
        var feedContext = $"seq={entry.Sequence} kind={entry.Kind} movement={entry.MovementKind?.ToString() ?? "none"}"; // DEBUG-LOG-ONLY correlation text.
        foreach (var session in mapSessions)
        {
            try
            {
                await session.NotifyMonsterMovedAsync(actor, movementKind, entry.Instance, cancellationToken, feedContext);
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
        await using (var session = new MapClientSession(sessionId, client, _charConnector, _world, _worldRuntime, FanOutPlayerAttackActionAsync, _lethalAttackGate, ReportAuthenticatedUserCount))
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
                // Covers every disconnect path (normal close, IOException, OperationCanceledException,
                // an unexpected exception) uniformly - a session that never authenticated simply does
                // not change the count (it was never included in it), and this can never go negative
                // since the count is always recomputed fresh from the current _sessions contents.
                ReportAuthenticatedUserCount();
            }
        }

        MapLogger.Info($"[iRO MAP DEBUG] Client disconnected: {endpoint}");
    }
}
