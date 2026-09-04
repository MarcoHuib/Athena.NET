using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;
using Athena.Net.World.Telemetry;
using Orleans;
using System.Diagnostics.CodeAnalysis;
using Athena.Net.World.Runtime;

namespace Athena.Net.World;

public sealed class WorldPartitionGrain(IWorldPartitionResolver resolver, IMovementPathProvider movementPathProvider, TimeProvider timeProvider, IMapCollisionProvider collisionProvider, WorldMonsterTouchedWindowOptions? touchedWindowOptions = null) : Grain, IWorldPartitionGrain
{
    private readonly Dictionary<string, MapRuntime> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _mapByCharacter = [];
    private readonly Dictionary<Guid, TransferRecord> _outgoing = [];
    private readonly Dictionary<Guid, IncomingRecord> _incoming = [];
    private readonly Dictionary<uint, ActiveMovement> _movements = [];
    // Phase 2B monster simulation: one WorldMonsterMapSimulation per map this partition owns,
    // created lazily on first touch (LoadMonsterSpawnsAsync or an implicit PollMonsterFeedAsync
    // bootstrap) - never pre-created for every map the resolver could theoretically route here,
    // matching this project's existing "never pre-materialize state for a map nobody has touched"
    // convention (see Map(mapId)'s own identical lazy pattern used for player presence below).
    //
    // Active/touched-map policy (see this project's own "Inactive-map semantics" design): a map's
    // simulation keeps ticking for as long as it is "touched" (a spawn load, a feed poll, or an
    // engagement mutation - anything that represents genuine current activity) within
    // TouchedWindow of the grain's own tick loop's current time. A simulation that falls outside
    // that window is UNLOADED entirely (never merely paused - see WorldMonsterMapSimulation.Unload's
    // own doc comment for why), and the next touch after that rebuilds it fresh under a new
    // SimulationEpoch. This is what makes the policy immune to the giant-catch-up-tick failure mode
    // by construction: there is no elapsed-time state left to feed a stale gap into once a map has
    // been unloaded.
    private readonly TimeSpan _touchedWindow = touchedWindowOptions?.Window ?? TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, WorldMonsterMapSimulation> _monsterSimulations = new(StringComparer.OrdinalIgnoreCase);
    private Orleans.Runtime.IGrainTimer? _monsterTickTimer;
    private DateTimeOffset? _lastMonsterTickAt;
    private string PartitionId => this.GetPrimaryKeyString();

    // Every call site that represents genuine current activity for a map (spawn load, an
    // engagement mutation) goes through this - it both lazily creates the simulation on first
    // touch AND records that touch's timestamp, which the tick loop itself later consults to
    // decide unload-vs-keep-ticking. Touching an UNLOADED simulation would defeat the whole
    // touched-window policy (a bare poll against a map nobody has actually loaded would then
    // "reset the clock" forever, never letting it be reaped) - see PollMonsterFeedAsync's own use
    // of the non-touching FindOrCreateMonsterSimulation lookup for exactly this reason. This method
    // is therefore reserved for RPCs that only make sense against (and thus only ever succeed
    // against) an ALREADY-loaded simulation, or that ARE the load itself.
    private WorldMonsterMapSimulation MonsterSimulation(string mapId)
    {
        var simulation = FindOrCreateMonsterSimulation(mapId);
        simulation.Touch(timeProvider.GetUtcNow());
        TouchActivationLifetime();
        return simulation;
    }

    // RegisterGrainTimer does NOT by itself keep an otherwise-idle activation alive against
    // Orleans' ordinary idle-activation collection - a touched map's simulation must still keep
    // ticking through its full touched window even when no player/session RPC arrives during that
    // window (the tick loop itself is what would poll/unload it, but the tick loop cannot run once
    // the activation has already been collected). DelayDeactivation extends the activation's
    // minimum remaining lifetime from "now" - called on every genuine touch, so it re-extends
    // itself continuously for as long as the map keeps being touched, and simply stops being
    // re-extended once touches stop, letting the activation become collectible again after the
    // touched window (plus one tick's margin, so the final tick that unloads the simulation still
    // gets to run) has actually elapsed. This deliberately does NOT make the partition permanently
    // immortal - an activation with nothing touched will age out and be collected normally.
    private void TouchActivationLifetime() => this.DelayDeactivation(_touchedWindow + MonsterTickInterval);

    // Non-touching lookup - lazily creates the simulation record if this map has never been seen
    // at all (so BuildPage has something to report SpawnInitializationRequired against), but never
    // extends LastTouchedUtc merely for looking it up. A caller decides for itself whether the
    // lookup it just performed should also count as a touch (see PollMonsterFeedAsync's own
    // "touch only if genuinely loaded" policy).
    private WorldMonsterMapSimulation FindOrCreateMonsterSimulation(string mapId)
    {
        if (_monsterSimulations.TryGetValue(mapId, out var simulation)) return simulation;
        simulation = new WorldMonsterMapSimulation(mapId, timeProvider.GetUtcNow());
        _monsterSimulations[mapId] = simulation;
        return simulation;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        WorldTelemetry.PartitionActivations.Add(1, new KeyValuePair<string, object?>("world.partition.id", PartitionId));
        // A grain TIMER (per the approved feed guarantees: "grain timer, not reminder") - fires only
        // while this activation is alive; an idle-collected/reactivated grain simply starts a fresh
        // timer on its next activation, which is exactly consistent with this same activation-loss
        // event already meaning every map's simulation state is gone too (see the plan's own World
        // activation-lifecycle statement - no new durable persistence is introduced here).
        _monsterTickTimer = this.RegisterGrainTimer(MonsterTickAsync, MonsterTickInterval, MonsterTickInterval);
        return base.OnActivateAsync(cancellationToken);
    }

    // Matches MapServer's own pre-existing 100ms cadence (MapTcpServer.MonsterTickInterval) - not
    // independently chosen. The accepted movement-jump investigation finding is carried forward as
    // telemetry here (see WorldTelemetry.MonsterTick* metrics below), never as speculative
    // mitigation - see the plan's own "Movement-jump finding" design decision for why.
    private static readonly TimeSpan MonsterTickInterval = TimeSpan.FromMilliseconds(100);

    private Task MonsterTickAsync()
    {
        var tickStartedAt = timeProvider.GetTimestamp();
        var now = timeProvider.GetUtcNow();
        if (_lastMonsterTickAt is { } previous)
        {
            var elapsedSinceLast = (now - previous).TotalMilliseconds;
            WorldTelemetry.MonsterTickElapsedSinceLast.Record(elapsedSinceLast, new KeyValuePair<string, object?>("world.partition.id", PartitionId));
            // "Late" means this tick's own real elapsed gap materially exceeded the configured
            // cadence - the exact, honestly-measured condition the movement-jump investigation
            // identified as the mechanism behind the observed client-visible snap; recorded so it
            // is finally OBSERVABLE, never silently invisible the way the old MapServer tick loop
            // left it (that loop never measured its own elapsed time at all).
            if (elapsedSinceLast > MonsterTickInterval.TotalMilliseconds * 1.5)
                WorldTelemetry.MonsterTickLate.Add(1, new KeyValuePair<string, object?>("world.partition.id", PartitionId));
        }
        _lastMonsterTickAt = now;

        // Active/touched-map policy: unload any simulation whose last touch has fallen outside the
        // window BEFORE ticking anything - an unloaded map is never ticked the same pass it was
        // unloaded in, and a just-unloaded map's next touch (a fresh LoadMonsterSpawnsAsync/
        // PollMonsterFeedAsync call) is what rebuilds it, not this timer.
        List<string>? expired = null;
        foreach (var (mapId, simulation) in _monsterSimulations)
        {
            if (now - simulation.LastTouchedUtc > _touchedWindow) (expired ??= []).Add(mapId);
        }
        if (expired is not null)
        {
            foreach (var mapId in expired) _monsterSimulations[mapId].Unload();
        }

        foreach (var simulation in _monsterSimulations.Values)
        {
            if (simulation.Registry is null) continue; // Never loaded, or just unloaded above - nothing to tick.
            simulation.Tick(now, ResolvePresenceForEngagement, IsWalking);
        }

        WorldTelemetry.MonsterTickProcessingDuration.Record(timeProvider.GetElapsedTime(tickStartedAt).TotalMilliseconds, new KeyValuePair<string, object?>("world.partition.id", PartitionId));
        return Task.CompletedTask;
    }

    private WorldPlayerPresence? ResolvePresenceForEngagement(uint characterId) => TryFind(characterId, out var presence) ? presence : null;

    public Task<WorldPresenceRegistration> RegisterPresenceAsync(WorldPlayerPresence presence)
    {
        Validate(presence);
        var mapId = RequireOwnedMap(presence.MapId);
        presence = presence with { MapId = mapId };
        if (_incoming.Values.Any(x => !x.Committed && x.Presence.CharacterId == presence.CharacterId))
            return Task.FromResult(Registration(mapId, WorldPresenceRegistrationStatus.Conflict));
        if (TryFind(presence.CharacterId, out var existing))
        {
            if (existing.PresenceId != presence.PresenceId || existing.ActorId != presence.ActorId || !Same(existing.MapId, mapId))
                return Task.FromResult(Registration(mapId, WorldPresenceRegistrationStatus.Conflict));
            Map(mapId).Players[presence.CharacterId] = presence;
            return Task.FromResult(Registration(mapId, WorldPresenceRegistrationStatus.AlreadyRegistered));
        }
        Add(presence);
        return Task.FromResult(Registration(mapId, WorldPresenceRegistrationStatus.Registered));
    }

    public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId)
    {
        mapId = RequireOwnedMap(mapId);
        if (!TryFind(characterId, out var existing)) return Task.FromResult(Unregistration(mapId, WorldPresenceUnregistrationStatus.AlreadyAbsent));
        if (existing.PresenceId != presenceId) return Task.FromResult(Unregistration(mapId, WorldPresenceUnregistrationStatus.PresenceMismatch));
        if (!Same(existing.MapId, mapId)) return Task.FromResult(Unregistration(mapId, WorldPresenceUnregistrationStatus.MapMismatch));
        Remove(existing);
        return Task.FromResult(Unregistration(mapId, WorldPresenceUnregistrationStatus.Removed));
    }

    public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command)
    {
        var mapId = RequireOwnedMap(command.MapId);
        if (!TryFind(command.CharacterId, out var current)) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.NotFound, null));
        if (current.PresenceId != command.PresenceId) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.PresenceMismatch, current));
        if (!Same(current.MapId, mapId) || current.X != command.FromX || current.Y != command.FromY)
            return Task.FromResult(new WorldMovementResult(WorldMovementStatus.SourceMismatch, current));
        if (_outgoing.Values.Any(x => !x.Finalized && x.Source.CharacterId == command.CharacterId))
            return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Rejected, current));
        var path = movementPathProvider.ComputePath(mapId, command.FromX, command.FromY, command.DestinationX, command.DestinationY)
            .Select(cell => new WorldPosition(cell.X, cell.Y)).ToArray();
        if (path.Length < 2) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Rejected, current));
        var movementId = Guid.NewGuid();
        _movements[command.CharacterId] = new(movementId, command.PresenceId, mapId, path);
        return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Moved, current, path, movementId));
    }

    public Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command)
    {
        var mapId = RequireOwnedMap(command.MapId);
        if (!TryFind(command.CharacterId, out var current)) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.NotFound, null));
        if (current.PresenceId != command.PresenceId) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.PresenceMismatch, current));
        if (!_movements.TryGetValue(command.CharacterId, out var movement) || movement.MovementId != command.MovementId || !Same(movement.MapId, mapId))
            return Task.FromResult(new WorldMovementResult(WorldMovementStatus.SourceMismatch, current));
        if (command.DestinationIndex < 1 || command.DestinationIndex >= movement.Path.Count)
            return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Rejected, current));
        movement.Path = movement.Path.Take(command.DestinationIndex + 1).ToArray();
        return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Moved, current, movement.Path, movement.MovementId));
    }

    public Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command)
    {
        var mapId = RequireOwnedMap(command.MapId);
        if (!TryFind(command.CharacterId, out var current)) return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.NotFound, null));
        if (current.PresenceId != command.PresenceId) return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.PresenceMismatch, current));
        if (!Same(current.MapId, mapId) || current.X != command.ExpectedX || current.Y != command.ExpectedY)
        {
            if (current.X == command.NewX && current.Y == command.NewY) return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.AlreadyAdvanced, current));
            return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.SourceMismatch, current));
        }
        if (!_movements.TryGetValue(command.CharacterId, out var movement) || movement.MovementId != command.MovementId || !Same(movement.MapId, mapId))
            return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.StaleRoute, current));
        var nextIndex = movement.Position + 1;
        if (nextIndex >= movement.Path.Count || movement.Path[nextIndex].X != command.NewX || movement.Path[nextIndex].Y != command.NewY)
            return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.Rejected, current));
        var advanced = current with { X = command.NewX, Y = command.NewY };
        Map(mapId).Players[command.CharacterId] = advanced;
        movement.Position = nextIndex;
        if (nextIndex == movement.Path.Count - 1) _movements.Remove(command.CharacterId);
        return Task.FromResult(new WorldMovementAdvanceResult(WorldMovementAdvanceStatus.Advanced, advanced));
    }

    public Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command)
    {
        var mapId = RequireOwnedMap(command.MapId);
        if (!TryFind(command.CharacterId, out var current)) return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.PresenceNotFound, null));
        if (current.PresenceId != command.PresenceId) return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.PresenceMismatch, current));
        if (!_movements.TryGetValue(command.CharacterId, out var movement))
            return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.AlreadyAbsent, current));
        if (movement.MovementId != command.MovementId || !Same(movement.MapId, mapId))
            return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.SourceMismatch, current));
        _movements.Remove(command.CharacterId);
        return Task.FromResult(new WorldMovementCancellationResult(WorldMovementCancellationStatus.Cancelled, current));
    }

    public async Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command)
    {
        if (_outgoing.TryGetValue(command.TransferId, out var replay))
        {
            if (replay.Command != command) return new(WorldTransferStatus.Conflict, replay.Type, replay.Destination);
            if (replay.Finalized) return new(WorldTransferStatus.AlreadyCompleted, replay.Type, replay.Destination);
            return await ContinueCrossPartitionAsync(replay);
        }
        var sourceMap = RequireOwnedMap(command.SourceMapId);
        if (!TryFind(command.CharacterId, out var current)) return new(WorldTransferStatus.NotFound, WorldTransferType.SamePartition, null);
        if (current.PresenceId != command.PresenceId || !Same(current.MapId, sourceMap)) return new(WorldTransferStatus.SourceMismatch, WorldTransferType.SamePartition, current);
        var destinationMap = WorldMapId.Normalize(command.DestinationMapId);
        var destinationPartition = resolver.ResolvePartition(destinationMap);
        var destination = current with { MapId = destinationMap, X = command.DestinationX, Y = command.DestinationY };
        var type = Same(destinationPartition, PartitionId) ? WorldTransferType.SamePartition : WorldTransferType.CrossPartition;
        var normalized = command with { SourceMapId = sourceMap, DestinationMapId = destinationMap };
        var record = new TransferRecord(normalized, current, destination, destinationPartition, type);
        _outgoing.Add(command.TransferId, record);
        if (type == WorldTransferType.SamePartition)
        {
            RequireOwnedMap(destinationMap); Remove(current); Add(destination); record.Finalized = true;
            return new(WorldTransferStatus.Completed, type, destination);
        }
        return await ContinueCrossPartitionAsync(record);
    }

    private async Task<WorldTransferResult> ContinueCrossPartitionAsync(TransferRecord record)
    {
        var target = GrainFactory.GetGrain<IWorldPartitionGrain>(record.DestinationPartition);
        var payload = new IncomingWorldTransfer(record.Command.TransferId, record.Source, PartitionId, record.Source.MapId,
            record.Destination.MapId, record.Destination.X, record.Destination.Y);
        var prepared = await target.PrepareIncomingTransferAsync(payload);
        if (prepared.Status == IncomingTransferStatus.Conflict) return new(WorldTransferStatus.Conflict, record.Type, record.Source);
        var committed = await target.CommitIncomingTransferAsync(record.Command.TransferId);
        if (committed.Status is not (IncomingTransferStatus.Committed or IncomingTransferStatus.AlreadyCommitted))
            return new(WorldTransferStatus.Conflict, record.Type, committed.Presence);
        var finalized = await FinalizeOutgoingTransferAsync(record.Command.TransferId);
        if (finalized.Status is not (OutgoingTransferStatus.Finalized or OutgoingTransferStatus.AlreadyFinalized))
            return new(WorldTransferStatus.Conflict, record.Type, committed.Presence);
        return new(WorldTransferStatus.Completed, record.Type, committed.Presence);
    }

    public Task<IncomingTransferResult> PrepareIncomingTransferAsync(IncomingWorldTransfer transfer)
    {
        var destinationMap = RequireOwnedMap(transfer.DestinationMapId);
        if (_incoming.TryGetValue(transfer.TransferId, out var replay))
            return Task.FromResult(replay.Transfer == transfer
                ? new IncomingTransferResult(replay.Committed ? IncomingTransferStatus.AlreadyCommitted : IncomingTransferStatus.AlreadyPrepared, replay.Presence)
                : new IncomingTransferResult(IncomingTransferStatus.Conflict, replay.Presence));
        var reserved = _incoming.Values.FirstOrDefault(x => !x.Committed && x.Presence.CharacterId == transfer.Presence.CharacterId);
        if (reserved is not null) return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.Conflict, reserved.Presence));
        if (TryFind(transfer.Presence.CharacterId, out var owner) && owner.PresenceId != transfer.Presence.PresenceId)
            return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.Conflict, owner));
        var presence = transfer.Presence with { MapId = destinationMap, X = transfer.DestinationX, Y = transfer.DestinationY };
        _incoming.Add(transfer.TransferId, new(transfer, presence));
        return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.Prepared, presence));
    }

    public Task<IncomingTransferResult> CommitIncomingTransferAsync(Guid transferId)
    {
        if (!_incoming.TryGetValue(transferId, out var incoming)) return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.NotFound, null));
        if (incoming.Committed) return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.AlreadyCommitted, incoming.Presence));
        if (TryFind(incoming.Presence.CharacterId, out var owner) && owner.PresenceId != incoming.Presence.PresenceId)
            return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.Conflict, owner));
        if (owner is not null) Remove(owner);
        Add(incoming.Presence); incoming.Committed = true;
        return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.Committed, incoming.Presence));
    }

    public Task<OutgoingTransferResult> FinalizeOutgoingTransferAsync(Guid transferId)
    {
        if (!_outgoing.TryGetValue(transferId, out var outgoing)) return Task.FromResult(new OutgoingTransferResult(OutgoingTransferStatus.NotFound));
        if (outgoing.Finalized) return Task.FromResult(new OutgoingTransferResult(OutgoingTransferStatus.AlreadyFinalized));
        if (!TryFind(outgoing.Source.CharacterId, out var current) || current.PresenceId != outgoing.Source.PresenceId || !Same(current.MapId, outgoing.Source.MapId))
            return Task.FromResult(new OutgoingTransferResult(OutgoingTransferStatus.Stale));
        Remove(current); outgoing.Finalized = true;
        return Task.FromResult(new OutgoingTransferResult(OutgoingTransferStatus.Finalized));
    }

    public Task<WorldMapSnapshot> GetMapSnapshotAsync(string mapId)
    {
        mapId = RequireOwnedMap(mapId);
        var players = _maps.TryGetValue(mapId, out var map) ? map.Players.Values.OrderBy(x => x.CharacterId).ToArray() : [];
        return Task.FromResult(new WorldMapSnapshot(PartitionId, mapId, players));
    }

    // Phase 2B monster-simulation contract members. See WorldMonsterMapSimulation's own doc
    // comment for the per-map state this delegates to, and IWorldPartitionGrain.cs's own doc
    // comment for the full scope boundary (simulation authority only - damage/quest/HP stay
    // MapServer-local). No timer yet (Step 3) - these members are reachable only via direct calls
    // for this step.

    // Leases EXACTLY the actor IDs a batch needs, in ONE call to the global
    // IActorIdBlockAuthorityGrain (ActorIdBlockAuthorityGrainKey.WellKnownKey - the same one every
    // other actor-ID consumer in this cluster leases from) - deliberately NOT
    // LeasedBlockActorIdAllocator's own default 10,000-ID block size. That allocator re-leases a
    // fresh (default-sized) block internally whenever its current one is exhausted, which is the
    // right behavior for a long-lived, many-small-allocations consumer (MapServer's own NPC/warp
    // ActorId allocator) but wrong here: this map's simulation is rebuilt wholesale on every
    // load/inactive-map-rebuild (Step 3), and a fresh LeasedBlockActorIdAllocator built fresh each
    // time would silently discard whatever fraction of a 10,000 block a small spawn set didn't
    // consume, every single rebuild - a real, compounding ID-domain leak once Step 3's unload/
    // rebuild policy starts recreating simulations regularly. A map's own required count is known
    // exactly (sum of every spawn's Count) before MonsterRegistry needs anything, so this leases
    // that exact size directly against IActorIdBlockAuthorityGrain.LeaseBlockAsync - never through
    // LeasedBlockActorIdAllocator's own re-lease-on-exhaustion machinery - and hands out the leased
    // range from a simple synchronous local cursor.
    public async Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch)
    {
        var mapId = RequireOwnedMap(batch.MapId);
        var simulation = MonsterSimulation(mapId);
        if (!simulation.AllSpawnsBelongToThisMap(batch.Spawns))
            return new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.SpawnMapMismatch, simulation.SimulationEpoch);
        if (batch.Spawns.Any(spawn => spawn.Count < 0))
            throw new ArgumentException($"World monster spawn batch for map '{mapId}' contains a spawn with a negative Count.", nameof(batch));

        // World computes its OWN canonical fingerprint from the actual batch content - the
        // caller-supplied Fingerprint is never trusted as proof of identity (see
        // WorldMonsterSpawnBatch's own doc comment). A caller whose own claimed fingerprint
        // disagrees with what World independently computes gets a distinct rejection from an
        // ordinary "content genuinely changed" reload, since that specifically indicates a caller
        // bug (its own hashing disagrees with its own payload), not a legitimate new spawn set.
        var computedFingerprint = WorldMonsterMapSimulation.ComputeContentFingerprint(batch.Spawns);
        if (!string.IsNullOrEmpty(batch.Fingerprint) && !string.Equals(batch.Fingerprint, computedFingerprint, StringComparison.Ordinal))
            return new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.CallerFingerprintMismatch, simulation.SimulationEpoch);

        if (simulation.CurrentFingerprint is { } existingFingerprint)
        {
            if (string.Equals(existingFingerprint, computedFingerprint, StringComparison.Ordinal))
                return new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.AlreadyLoaded, simulation.SimulationEpoch);
            return new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.ContentMismatch, simulation.SimulationEpoch);
        }

        // checked{} surfaces an OverflowException rather than silently wrapping if a batch's total
        // instance count (implausible in practice, but not structurally impossible given
        // per-spawn Count is caller-supplied) would overflow the uint domain this leases from.
        var requiredCount = checked((uint)simulation.PendingActorIdCount(batch.Spawns));
        Func<uint> allocateActorId;
        if (requiredCount == 0)
        {
            // A zero-monster batch (every spawn's Count is 0, or Spawns is empty) must never lease
            // a block at all - there is nothing to allocate an ActorId for.
            allocateActorId = static () => throw new InvalidOperationException("No monster instances require an ActorId for this batch - allocateActorId should never be invoked.");
        }
        else
        {
            var leasedBlock = await GrainFactory.GetGrain<IActorIdBlockAuthorityGrain>(ActorIdBlockAuthorityGrainKey.WellKnownKey)
                .LeaseBlockAsync($"world-monster-simulation:{PartitionId}:{mapId}", requiredCount);
            var cursor = (ulong)leasedBlock.StartInclusive - 1;
            allocateActorId = () =>
            {
                cursor++;
                if (cursor >= leasedBlock.EndExclusive)
                    throw new InvalidOperationException($"World monster spawn batch for map '{mapId}' leased exactly {requiredCount} ActorId(s) but MonsterRegistry attempted to allocate more than that - PendingActorIdCount disagreed with MonsterRegistry's own per-spawn expansion.");
                return (uint)cursor;
            };
        }

        simulation.Rebuild(batch.Spawns, computedFingerprint, allocateActorId, timeProvider, collisionProvider, movementPathProvider);
        return new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.Loaded, simulation.SimulationEpoch);
    }

    public Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId)
    {
        mapId = RequireOwnedMap(mapId);
        // Non-touching lookup: a poll against a map whose simulation is not (yet, or no longer)
        // loaded must not extend LastTouchedUtc, or an unloaded simulation could never be reaped -
        // a MapServer that merely polls (without ever successfully loading spawns) would otherwise
        // pin it "touched" forever. Only a genuinely loaded simulation's touched window is extended
        // by polling it, since only a loaded simulation is actually doing anything worth keeping alive.
        var simulation = FindOrCreateMonsterSimulation(mapId);
        var page = simulation.BuildPage(cursor);
        if (simulation.IsLoaded)
        {
            simulation.Touch(timeProvider.GetUtcNow());
            TouchActivationLifetime();
        }
        return Task.FromResult(page);
    }

    public Task<WorldMonsterDeathResult> TryMarkMonsterDeadAsync(WorldMonsterLifeReference reference)
    {
        var mapId = RequireOwnedMap(reference.MapId);
        var simulation = MonsterSimulation(mapId);
        if (!simulation.SimulationEpoch.Equals(reference.SimulationEpoch) || !simulation.TryFind(reference.ActorId, out var instance) || !simulation.MatchesLife(instance, reference))
            return Task.FromResult(new WorldMonsterDeathResult(WorldMonsterDeathStatus.StaleLifeReference));
        return Task.FromResult(new WorldMonsterDeathResult(simulation.MarkDead(instance)));
    }

    public Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command)
    {
        var reference = command.Life;
        var mapId = RequireOwnedMap(reference.MapId);
        var simulation = MonsterSimulation(mapId);
        if (!simulation.SimulationEpoch.Equals(reference.SimulationEpoch) || !simulation.TryFind(reference.ActorId, out var instance) || !simulation.MatchesLife(instance, reference))
            return Task.FromResult(new WorldMonsterAttackedResult(WorldMonsterAttackedStatus.StaleLifeReference));
        // The attacker's presence must still be the grain's own CURRENT registration for that
        // CharacterId - a stale presence (e.g. from before a disconnect/reconnect) must never
        // acquire a target, exactly like a stale epoch/incarnation must not (see
        // WorldPlayerTargetReference's own doc comment).
        if (!TryFind(command.AttackerCharacterId, out var attackerPresence) || attackerPresence.PresenceId != command.AttackerPresenceId)
            return Task.FromResult(new WorldMonsterAttackedResult(WorldMonsterAttackedStatus.StaleAttackerPresence));
        // A current PresenceId alone is not enough - a presence that is now dead, or has moved to
        // another map since the hit that triggered this call, must never acquire (or keep) a
        // target either. This mirrors the exact validity check WorldMonsterEngagementRules.Evaluate
        // itself would apply on the very next tick - reject here rather than accept-then-immediately
        // -Unlock on the next tick (see TryAcquireEngagement's own doc comment for why storing an
        // EngagedTarget the shared rules would immediately Unlock must never happen).
        if (!attackerPresence.IsAlive || !string.Equals(attackerPresence.MapId, mapId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new WorldMonsterAttackedResult(WorldMonsterAttackedStatus.AttackerNotEngageable));
        var target = new WorldPlayerTargetReference(command.AttackerCharacterId, command.AttackerPresenceId);
        return Task.FromResult(new WorldMonsterAttackedResult(
            simulation.TryAcquireEngagement(instance, target, attackerPresence, IsWalking(command.AttackerCharacterId))));
    }

    // Whether the grain's own authoritative movement state currently has this character mid-walk -
    // an active entry in _movements IS exactly that condition, matching how AdvanceMovementAsync/
    // CancelMovementAsync already treat _movements membership as the single source of truth for
    // "is this character currently walking" elsewhere in this same grain.
    private bool IsWalking(uint characterId) => _movements.ContainsKey(characterId);

    public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query)
    {
        var reference = query.Life;
        var mapId = RequireOwnedMap(reference.MapId);
        var simulation = MonsterSimulation(mapId);
        if (!simulation.SimulationEpoch.Equals(reference.SimulationEpoch) || !simulation.TryFind(reference.ActorId, out var instance) || !simulation.MatchesLife(instance, reference))
            return Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.StaleLifeReference));
        // TargetNotFound (the character has no presence registered at all under this partition)
        // and StaleTargetPresence (the character IS registered, just under a DIFFERENT PresenceId
        // than the one this query presents) are kept as separate, distinct diagnostics - collapsing
        // them would hide which of two genuinely different failure modes actually occurred.
        if (!TryFind(query.TargetCharacterId, out var targetPresence))
            return Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.TargetNotFound));
        if (targetPresence.PresenceId != query.TargetPresenceId)
            return Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.StaleTargetPresence));
        if (!targetPresence.IsAlive)
            return Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.TargetDead));
        var currentTarget = simulation.CurrentTarget(instance.ActorId);
        if (currentTarget is null || currentTarget.CharacterId != query.TargetCharacterId || currentTarget.PresenceId != query.TargetPresenceId)
            return Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.NotCurrentTarget));
        // The SAME target-walking semantics NotifyMonsterAttackedAsync/Step 3's own engagement tick
        // use - the walking-target +1 range bonus (WorldMonsterEngagementRules' own pinned trace)
        // must not silently default to false here just because this is a different call site.
        var range = WorldMonsterEngagementRules.Evaluate(instance, targetPresence, IsWalking(query.TargetCharacterId));
        return Task.FromResult(new WorldMonsterAttackWindowResult(
            range is WorldMonsterEngagementDecision.InAttackRange ? WorldMonsterAttackWindowStatus.Valid : WorldMonsterAttackWindowStatus.OutOfRange));
    }

    public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(WorldPresenceLifeStateUpdate update)
    {
        if (!TryFind(update.CharacterId, out var current)) return Task.FromResult(new WorldPresenceLifeStateResult(WorldPresenceLifeStateStatus.NotFound));
        if (current.PresenceId != update.PresenceId) return Task.FromResult(new WorldPresenceLifeStateResult(WorldPresenceLifeStateStatus.StalePresence));
        Map(current.MapId).Players[current.CharacterId] = current with { IsAlive = update.IsAlive };
        return Task.FromResult(new WorldPresenceLifeStateResult(WorldPresenceLifeStateStatus.Updated));
    }

    private string RequireOwnedMap(string mapId)
    {
        var normalized = WorldMapId.Normalize(mapId); var owner = resolver.ResolvePartition(normalized);
        if (!Same(owner, PartitionId)) throw new InvalidOperationException($"World partition '{PartitionId}' cannot own map '{normalized}'; its owner is '{owner}'.");
        return normalized;
    }
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private MapRuntime Map(string mapId) => _maps.TryGetValue(mapId, out var map) ? map : _maps[mapId] = new();
    private void Add(WorldPlayerPresence presence) { Map(presence.MapId).Players[presence.CharacterId] = presence; _mapByCharacter[presence.CharacterId] = presence.MapId; }
    private bool TryFind(uint characterId, [NotNullWhen(true)] out WorldPlayerPresence? presence)
    { if (_mapByCharacter.TryGetValue(characterId, out var mapId) && Map(mapId).Players.TryGetValue(characterId, out var found)) { presence = found; return true; } presence = null; return false; }
    private void Remove(WorldPlayerPresence presence) { Map(presence.MapId).Players.Remove(presence.CharacterId); _mapByCharacter.Remove(presence.CharacterId); _movements.Remove(presence.CharacterId); }
    private int Count => _mapByCharacter.Count;
    private WorldPresenceRegistration Registration(string mapId, WorldPresenceRegistrationStatus status) => new(PartitionId, mapId, status, Count);
    private WorldPresenceUnregistration Unregistration(string mapId, WorldPresenceUnregistrationStatus status) => new(PartitionId, mapId, status, Count);
    private static void Validate(WorldPlayerPresence presence) { if (presence.PresenceId == Guid.Empty || presence.ActorId == 0 || presence.CharacterId == 0) throw new ArgumentException("Presence identity is invalid.", nameof(presence)); }
    private sealed class MapRuntime { public Dictionary<uint, WorldPlayerPresence> Players { get; } = []; }
    private sealed class TransferRecord(WorldTransferCommand command, WorldPlayerPresence source, WorldPlayerPresence destination, string destinationPartition, WorldTransferType type)
    { public WorldTransferCommand Command { get; } = command; public WorldPlayerPresence Source { get; } = source; public WorldPlayerPresence Destination { get; } = destination; public string DestinationPartition { get; } = destinationPartition; public WorldTransferType Type { get; } = type; public bool Finalized { get; set; } }
    private sealed class IncomingRecord(IncomingWorldTransfer transfer, WorldPlayerPresence presence) { public IncomingWorldTransfer Transfer { get; } = transfer; public WorldPlayerPresence Presence { get; } = presence; public bool Committed { get; set; } }
    private sealed class ActiveMovement(Guid movementId, Guid presenceId, string mapId, IReadOnlyList<WorldPosition> path)
    { public Guid MovementId { get; } = movementId; public Guid PresenceId { get; } = presenceId; public string MapId { get; } = mapId; public IReadOnlyList<WorldPosition> Path { get; set; } = path; public int Position { get; set; } }
}
