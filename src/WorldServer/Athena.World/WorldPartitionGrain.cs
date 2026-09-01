using Athena.Net.World.Contracts;
using Athena.Net.World.Telemetry;
using Orleans;
using System.Diagnostics.CodeAnalysis;

namespace Athena.Net.World;

public sealed class WorldPartitionGrain(IWorldPartitionResolver resolver) : Grain, IWorldPartitionGrain
{
    private readonly Dictionary<string, MapRuntime> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _mapByCharacter = [];
    private readonly Dictionary<Guid, TransferRecord> _outgoing = [];
    private readonly Dictionary<Guid, IncomingRecord> _incoming = [];
    private string PartitionId => this.GetPrimaryKeyString();

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
        var path = ComputePath(command.FromX, command.FromY, command.DestinationX, command.DestinationY);
        if (path.Count < 2) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Rejected, current));
        var moved = current with { X = command.DestinationX, Y = command.DestinationY };
        Map(mapId).Players[command.CharacterId] = moved;
        return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Moved, moved, path));
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

    private string RequireOwnedMap(string mapId)
    {
        var normalized = WorldMapId.Normalize(mapId); var owner = resolver.ResolvePartition(normalized);
        if (!Same(owner, PartitionId)) throw new InvalidOperationException($"World partition '{PartitionId}' cannot own map '{normalized}'; its owner is '{owner}'.");
        return normalized;
    }
    private static IReadOnlyList<WorldPosition> ComputePath(ushort x0, ushort y0, ushort x1, ushort y1)
    {
        var result = new List<WorldPosition>(); var x = (int)x0; var y = (int)y0; var dx = Math.Abs(x1 - x); var sx = x < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y); var sy = y < y1 ? 1 : -1; var error = dx + dy;
        while (true) { result.Add(new((ushort)x, (ushort)y)); if (x == x1 && y == y1) break; var twice = 2 * error; if (twice >= dy) { error += dy; x += sx; } if (twice <= dx) { error += dx; y += sy; } }
        return result;
    }
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private MapRuntime Map(string mapId) => _maps.TryGetValue(mapId, out var map) ? map : _maps[mapId] = new();
    private void Add(WorldPlayerPresence presence) { Map(presence.MapId).Players[presence.CharacterId] = presence; _mapByCharacter[presence.CharacterId] = presence.MapId; }
    private bool TryFind(uint characterId, [NotNullWhen(true)] out WorldPlayerPresence? presence)
    { if (_mapByCharacter.TryGetValue(characterId, out var mapId) && Map(mapId).Players.TryGetValue(characterId, out var found)) { presence = found; return true; } presence = null; return false; }
    private void Remove(WorldPlayerPresence presence) { Map(presence.MapId).Players.Remove(presence.CharacterId); _mapByCharacter.Remove(presence.CharacterId); }
    private int Count => _mapByCharacter.Count;
    private WorldPresenceRegistration Registration(string mapId, WorldPresenceRegistrationStatus status) => new(PartitionId, mapId, status, Count);
    private WorldPresenceUnregistration Unregistration(string mapId, WorldPresenceUnregistrationStatus status) => new(PartitionId, mapId, status, Count);
    private static void Validate(WorldPlayerPresence presence) { if (presence.PresenceId == Guid.Empty || presence.ActorId == 0 || presence.CharacterId == 0) throw new ArgumentException("Presence identity is invalid.", nameof(presence)); }
    private sealed class MapRuntime { public Dictionary<uint, WorldPlayerPresence> Players { get; } = []; }
    private sealed class TransferRecord(WorldTransferCommand command, WorldPlayerPresence source, WorldPlayerPresence destination, string destinationPartition, WorldTransferType type)
    { public WorldTransferCommand Command { get; } = command; public WorldPlayerPresence Source { get; } = source; public WorldPlayerPresence Destination { get; } = destination; public string DestinationPartition { get; } = destinationPartition; public WorldTransferType Type { get; } = type; public bool Finalized { get; set; } }
    private sealed class IncomingRecord(IncomingWorldTransfer transfer, WorldPlayerPresence presence) { public IncomingWorldTransfer Transfer { get; } = transfer; public WorldPlayerPresence Presence { get; } = presence; public bool Committed { get; set; } }
}
