using Athena.Net.World.Contracts;
using Athena.Net.World.Telemetry;
using Orleans;
using System.Diagnostics.CodeAnalysis;

namespace Athena.Net.World;

public sealed class WorldPartitionGrain : Grain, IWorldPartitionGrain
{
    private readonly Dictionary<string, MapRuntime> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _mapByCharacter = [];
    private readonly Dictionary<Guid, TransferRecord> _outgoing = [];
    private readonly Dictionary<Guid, IncomingRecord> _incoming = [];

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        WorldTelemetry.PartitionActivations.Add(1, new KeyValuePair<string, object?>("world.partition.id", this.GetPrimaryKeyString()));
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<WorldPresenceRegistration> RegisterPresenceAsync(WorldPlayerPresence presence)
    {
        Validate(presence);
        var mapId = WorldMapId.Normalize(presence.MapId);
        presence = presence with { MapId = mapId };
        if (TryFind(presence.CharacterId, out var existing))
        {
            if (existing.PresenceId != presence.PresenceId)
                return Task.FromResult(Registration(mapId, WorldPresenceRegistrationStatus.Conflict));
            if (!string.Equals(existing.MapId, mapId, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Registration(mapId, WorldPresenceRegistrationStatus.Conflict));
            Map(mapId).Players[presence.CharacterId] = presence;
            return Task.FromResult(Registration(mapId, WorldPresenceRegistrationStatus.AlreadyRegistered));
        }

        Map(mapId).Players.Add(presence.CharacterId, presence);
        _mapByCharacter.Add(presence.CharacterId, mapId);
        return Task.FromResult(Registration(mapId, WorldPresenceRegistrationStatus.Registered));
    }

    public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId)
    {
        mapId = WorldMapId.Normalize(mapId);
        if (!TryFind(characterId, out var existing)) return Task.FromResult(Unregistration(mapId, WorldPresenceUnregistrationStatus.AlreadyAbsent));
        if (existing.PresenceId != presenceId) return Task.FromResult(Unregistration(mapId, WorldPresenceUnregistrationStatus.PresenceMismatch));
        Remove(existing);
        return Task.FromResult(Unregistration(mapId, WorldPresenceUnregistrationStatus.Removed));
    }

    public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command)
    {
        var mapId = WorldMapId.Normalize(command.MapId);
        if (!TryFind(command.CharacterId, out var current)) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.NotFound, null));
        if (current.PresenceId != command.PresenceId) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.PresenceMismatch, current));
        if (!string.Equals(current.MapId, mapId, StringComparison.OrdinalIgnoreCase) || current.X != command.FromX || current.Y != command.FromY)
            return Task.FromResult(new WorldMovementResult(WorldMovementStatus.SourceMismatch, current));
        if (!ValidatePath(command)) return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Rejected, current));

        var moved = current with { X = command.DestinationX, Y = command.DestinationY };
        Map(mapId).Players[command.CharacterId] = moved;
        return Task.FromResult(new WorldMovementResult(WorldMovementStatus.Moved, moved));
    }

    public async Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command)
    {
        if (_outgoing.TryGetValue(command.TransferId, out var replay))
        {
            if (replay.Command != command) return new(WorldTransferStatus.Conflict, replay.Type, replay.Presence);
            if (replay.Completed) return new(WorldTransferStatus.AlreadyCompleted, replay.Type, replay.Presence);
            return await ContinueCrossPartitionAsync(replay);
        }

        if (!TryFind(command.CharacterId, out var current)) return new(WorldTransferStatus.NotFound, WorldTransferType.SamePartition, null);
        if (current.PresenceId != command.PresenceId || !string.Equals(current.MapId, WorldMapId.Normalize(command.SourceMapId), StringComparison.OrdinalIgnoreCase))
            return new(WorldTransferStatus.SourceMismatch, WorldTransferType.SamePartition, current);

        var destination = current with { MapId = WorldMapId.Normalize(command.DestinationMapId), X = command.DestinationX, Y = command.DestinationY };
        if (string.Equals(command.DestinationPartitionId, this.GetPrimaryKeyString(), StringComparison.OrdinalIgnoreCase))
        {
            Remove(current);
            Map(destination.MapId).Players.Add(destination.CharacterId, destination);
            _mapByCharacter.Add(destination.CharacterId, destination.MapId);
            _outgoing.Add(command.TransferId, new(command, destination, WorldTransferType.SamePartition, true));
            return new(WorldTransferStatus.Completed, WorldTransferType.SamePartition, destination);
        }

        var record = new TransferRecord(command, destination, WorldTransferType.CrossPartition, false);
        _outgoing.Add(command.TransferId, record);
        return await ContinueCrossPartitionAsync(record);
    }

    private async Task<WorldTransferResult> ContinueCrossPartitionAsync(TransferRecord record)
    {
        var target = GrainFactory.GetGrain<IWorldPartitionGrain>(record.Command.DestinationPartitionId);
        var prepared = await target.PrepareIncomingTransferAsync(record.Command);
        if (prepared.Status == IncomingTransferStatus.Conflict)
            return new(WorldTransferStatus.Conflict, record.Type, TryFind(record.Command.CharacterId, out var owner) ? owner : record.Presence);

        if (TryFind(record.Command.CharacterId, out var current) && current.PresenceId == record.Command.PresenceId) Remove(current);
        var committed = await target.CommitIncomingTransferAsync(record.Command.TransferId);
        if (committed.Status is IncomingTransferStatus.Committed or IncomingTransferStatus.AlreadyCommitted)
        {
            record.Completed = true;
            return new(WorldTransferStatus.Completed, record.Type, committed.Presence);
        }
        return new(WorldTransferStatus.Conflict, record.Type, committed.Presence);
    }

    public Task<IncomingTransferResult> PrepareIncomingTransferAsync(WorldTransferCommand command)
    {
        if (_incoming.TryGetValue(command.TransferId, out var existingTransfer))
            return Task.FromResult(existingTransfer.Command == command
                ? new IncomingTransferResult(existingTransfer.Committed ? IncomingTransferStatus.AlreadyCommitted : IncomingTransferStatus.AlreadyPrepared, existingTransfer.Presence)
                : new IncomingTransferResult(IncomingTransferStatus.Conflict, existingTransfer.Presence));
        if (TryFind(command.CharacterId, out var owner) && owner.PresenceId != command.PresenceId)
            return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.Conflict, owner));

        var incoming = new WorldPlayerPresence(command.PresenceId, owner?.ActorId ?? command.CharacterId, command.CharacterId,
            WorldMapId.Normalize(command.DestinationMapId), command.DestinationX, command.DestinationY);
        _incoming.Add(command.TransferId, new(command, incoming));
        return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.Prepared, incoming));
    }

    public Task<IncomingTransferResult> CommitIncomingTransferAsync(Guid transferId)
    {
        if (!_incoming.TryGetValue(transferId, out var incoming)) return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.NotFound, null));
        if (incoming.Committed) return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.AlreadyCommitted, incoming.Presence));
        if (TryFind(incoming.Presence.CharacterId, out var owner) && owner.PresenceId != incoming.Presence.PresenceId)
            return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.Conflict, owner));
        if (owner is not null) Remove(owner);
        Map(incoming.Presence.MapId).Players[incoming.Presence.CharacterId] = incoming.Presence;
        _mapByCharacter[incoming.Presence.CharacterId] = incoming.Presence.MapId;
        incoming.Committed = true;
        return Task.FromResult(new IncomingTransferResult(IncomingTransferStatus.Committed, incoming.Presence));
    }

    public Task<WorldMapSnapshot> GetMapSnapshotAsync(string mapId)
    {
        mapId = WorldMapId.Normalize(mapId);
        var players = _maps.TryGetValue(mapId, out var map) ? map.Players.Values.OrderBy(x => x.CharacterId).ToArray() : [];
        return Task.FromResult(new WorldMapSnapshot(this.GetPrimaryKeyString(), mapId, players));
    }

    private MapRuntime Map(string mapId) => _maps.TryGetValue(mapId, out var map) ? map : _maps[mapId] = new();
    private bool TryFind(uint characterId, [NotNullWhen(true)] out WorldPlayerPresence? presence)
    {
        if (_mapByCharacter.TryGetValue(characterId, out var mapId) && Map(mapId).Players.TryGetValue(characterId, out var found)) { presence = found; return true; }
        presence = null; return false;
    }
    private void Remove(WorldPlayerPresence presence) { Map(presence.MapId).Players.Remove(presence.CharacterId); _mapByCharacter.Remove(presence.CharacterId); }
    private int Count => _mapByCharacter.Count;
    private WorldPresenceRegistration Registration(string mapId, WorldPresenceRegistrationStatus status) => new(this.GetPrimaryKeyString(), mapId, status, Count);
    private WorldPresenceUnregistration Unregistration(string mapId, WorldPresenceUnregistrationStatus status) => new(this.GetPrimaryKeyString(), mapId, status, Count);
    private static bool ValidatePath(WorldMovementCommand command)
    {
        if (command.CollisionValidatedPath.Count == 0) return false;
        var first = command.CollisionValidatedPath[0]; var last = command.CollisionValidatedPath[^1];
        if (first.X != command.FromX || first.Y != command.FromY || last.X != command.DestinationX || last.Y != command.DestinationY) return false;
        for (var i = 1; i < command.CollisionValidatedPath.Count; i++)
            if (Math.Abs(command.CollisionValidatedPath[i].X - command.CollisionValidatedPath[i - 1].X) > 1 || Math.Abs(command.CollisionValidatedPath[i].Y - command.CollisionValidatedPath[i - 1].Y) > 1) return false;
        return true;
    }
    private static void Validate(WorldPlayerPresence presence) { if (presence.PresenceId == Guid.Empty || presence.ActorId == 0 || presence.CharacterId == 0) throw new ArgumentException("Presence identity is invalid.", nameof(presence)); }
    private sealed class MapRuntime { public Dictionary<uint, WorldPlayerPresence> Players { get; } = []; }
    private sealed class TransferRecord(WorldTransferCommand command, WorldPlayerPresence presence, WorldTransferType type, bool completed) { public WorldTransferCommand Command { get; } = command; public WorldPlayerPresence Presence { get; } = presence; public WorldTransferType Type { get; } = type; public bool Completed { get; set; } = completed; }
    private sealed class IncomingRecord(WorldTransferCommand command, WorldPlayerPresence presence) { public WorldTransferCommand Command { get; } = command; public WorldPlayerPresence Presence { get; } = presence; public bool Committed { get; set; } }
}
