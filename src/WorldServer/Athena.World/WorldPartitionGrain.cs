using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;
using Athena.Net.World.Telemetry;
using Orleans;
using System.Diagnostics.CodeAnalysis;
using Athena.Net.World.Runtime;

namespace Athena.Net.World;

public sealed class WorldPartitionGrain(IWorldPartitionResolver resolver, IMovementPathProvider movementPathProvider, TimeProvider timeProvider) : Grain, IWorldPartitionGrain
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
    // convention (see MonsterSimulation's own lazy-Map(mapId) pattern used for player presence
    // below). No Timer/reminder exists yet (Step 3) - this step is state/mutation/feed correctness
    // only, per the plan's own sequencing.
    private readonly Dictionary<string, WorldMonsterMapSimulation> _monsterSimulations = new(StringComparer.OrdinalIgnoreCase);
    private string PartitionId => this.GetPrimaryKeyString();
    private WorldMonsterMapSimulation MonsterSimulation(string mapId) =>
        _monsterSimulations.TryGetValue(mapId, out var simulation) ? simulation : _monsterSimulations[mapId] = new WorldMonsterMapSimulation(mapId);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        WorldTelemetry.PartitionActivations.Add(1, new KeyValuePair<string, object?>("world.partition.id", PartitionId));
        return base.OnActivateAsync(cancellationToken);
    }

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

    // Leases exactly the actor IDs a batch needs, synchronously, BEFORE constructing
    // MonsterRegistry (whose constructor requires a synchronous `Func<uint>` - see
    // WorldMonsterMapSimulation.Rebuild's own doc comment for why leasing cannot happen lazily
    // inside it). Reuses the SAME global IActorIdBlockAuthorityGrain every other actor-ID consumer
    // in this cluster leases from (ActorIdBlockAuthorityGrainKey.WellKnownKey) - monster ActorIds
    // and MapServer's own NPC/warp ActorIds share one domain, per that grain's own doc comment.
    public async Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch)
    {
        var mapId = RequireOwnedMap(batch.MapId);
        var simulation = MonsterSimulation(mapId);
        if (!simulation.AllSpawnsBelongToThisMap(batch.Spawns))
            return new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.SpawnMapMismatch, simulation.SimulationEpoch);

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

        var actorIdCount = simulation.PendingActorIdCount(batch.Spawns);
        var actorIds = new Queue<uint>(actorIdCount);
        var allocator = new LeasedBlockActorIdAllocator(async (blockSize, cancellationToken) =>
            await GrainFactory.GetGrain<IActorIdBlockAuthorityGrain>(ActorIdBlockAuthorityGrainKey.WellKnownKey)
                .LeaseBlockAsync($"world-monster-simulation:{PartitionId}:{mapId}", blockSize));
        for (var i = 0; i < actorIdCount; i++) actorIds.Enqueue(await allocator.AllocateAsync());

        simulation.Rebuild(batch.Spawns, computedFingerprint, () => actorIds.Dequeue(), timeProvider);
        return new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.Loaded, simulation.SimulationEpoch);
    }

    public Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId)
    {
        mapId = RequireOwnedMap(mapId);
        return Task.FromResult(MonsterSimulation(mapId).BuildPage(cursor));
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
        var target = new WorldPlayerTargetReference(command.AttackerCharacterId, command.AttackerPresenceId);
        return Task.FromResult(new WorldMonsterAttackedResult(simulation.TryAcquireEngagement(instance, target)));
    }

    public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query)
    {
        var reference = query.Life;
        var mapId = RequireOwnedMap(reference.MapId);
        var simulation = MonsterSimulation(mapId);
        if (!simulation.SimulationEpoch.Equals(reference.SimulationEpoch) || !simulation.TryFind(reference.ActorId, out var instance) || !simulation.MatchesLife(instance, reference))
            return Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.StaleLifeReference));
        if (!TryFind(query.TargetCharacterId, out var targetPresence) || targetPresence.PresenceId != query.TargetPresenceId)
            return Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.StaleTargetPresence));
        if (!targetPresence.IsAlive)
            return Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.TargetDead));
        var currentTarget = simulation.CurrentTarget(instance.ActorId);
        if (currentTarget is null || currentTarget.CharacterId != query.TargetCharacterId || currentTarget.PresenceId != query.TargetPresenceId)
            return Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.NotCurrentTarget));
        var range = WorldMonsterEngagementRules.Evaluate(instance, targetPresence, timeProvider.GetUtcNow());
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
