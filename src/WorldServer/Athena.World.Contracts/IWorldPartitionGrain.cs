using Orleans;

namespace Athena.Net.World.Contracts;

/// <summary>Coarse authority for multiple map runtimes. A map is not an Orleans grain.</summary>
public interface IWorldPartitionGrain : IGrainWithStringKey
{
    Task<WorldPresenceRegistration> RegisterPresenceAsync(WorldPlayerPresence presence);
    Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId);
    Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command);
    Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command);
    Task<IncomingTransferResult> PrepareIncomingTransferAsync(WorldTransferCommand command);
    Task<IncomingTransferResult> CommitIncomingTransferAsync(Guid transferId);
    Task<WorldMapSnapshot> GetMapSnapshotAsync(string mapId);
}

[GenerateSerializer]
public sealed record WorldPlayerPresence(
    [property: Id(0)] Guid PresenceId,
    [property: Id(1)] uint ActorId,
    [property: Id(2)] uint CharacterId,
    [property: Id(3)] string MapId,
    [property: Id(4)] ushort X,
    [property: Id(5)] ushort Y);

public enum WorldPresenceRegistrationStatus { Registered, AlreadyRegistered, Conflict }

[GenerateSerializer]
public sealed record WorldPresenceRegistration(
    [property: Id(0)] string PartitionId,
    [property: Id(1)] string MapId,
    [property: Id(2)] WorldPresenceRegistrationStatus Status,
    [property: Id(3)] int PresenceCount);

public enum WorldPresenceUnregistrationStatus { Removed, AlreadyAbsent, PresenceMismatch }

[GenerateSerializer]
public sealed record WorldPresenceUnregistration(
    [property: Id(0)] string PartitionId,
    [property: Id(1)] string MapId,
    [property: Id(2)] WorldPresenceUnregistrationStatus Status,
    [property: Id(3)] int PresenceCount);

public enum WorldMovementStatus { Moved, NotFound, PresenceMismatch, SourceMismatch, Rejected }

[GenerateSerializer]
public sealed record WorldMovementCommand(
    [property: Id(0)] Guid PresenceId,
    [property: Id(1)] uint CharacterId,
    [property: Id(2)] string MapId,
    [property: Id(3)] ushort FromX,
    [property: Id(4)] ushort FromY,
    [property: Id(5)] ushort DestinationX,
    [property: Id(6)] ushort DestinationY,
    [property: Id(7)] IReadOnlyList<WorldPosition> CollisionValidatedPath);

[GenerateSerializer]
public readonly record struct WorldPosition([property: Id(0)] ushort X, [property: Id(1)] ushort Y);

[GenerateSerializer]
public sealed record WorldMovementResult(
    [property: Id(0)] WorldMovementStatus Status,
    [property: Id(1)] WorldPlayerPresence? Presence);

public enum WorldTransferType { SamePartition, CrossPartition }
public enum WorldTransferStatus { Completed, AlreadyCompleted, Conflict, SourceMismatch, NotFound }

[GenerateSerializer]
public sealed record WorldTransferCommand(
    [property: Id(0)] Guid TransferId,
    [property: Id(1)] Guid PresenceId,
    [property: Id(2)] uint CharacterId,
    [property: Id(3)] string SourceMapId,
    [property: Id(4)] string DestinationMapId,
    [property: Id(5)] ushort DestinationX,
    [property: Id(6)] ushort DestinationY,
    [property: Id(7)] string DestinationPartitionId);

[GenerateSerializer]
public sealed record WorldTransferResult(
    [property: Id(0)] WorldTransferStatus Status,
    [property: Id(1)] WorldTransferType Type,
    [property: Id(2)] WorldPlayerPresence? Presence);

public enum IncomingTransferStatus { Prepared, AlreadyPrepared, Committed, AlreadyCommitted, Conflict, NotFound }

[GenerateSerializer]
public sealed record IncomingTransferResult(
    [property: Id(0)] IncomingTransferStatus Status,
    [property: Id(1)] WorldPlayerPresence? Presence);

[GenerateSerializer]
public sealed record WorldMapSnapshot(
    [property: Id(0)] string PartitionId,
    [property: Id(1)] string MapId,
    [property: Id(2)] IReadOnlyList<WorldPlayerPresence> Players);
