using Orleans;

namespace Athena.Net.World.Contracts;

/// <summary>
/// Coarse-grained authority for one logical Ragnarok map. Hot map simulation belongs inside this
/// boundary; individual monsters, cells, movement operations, and combat operations are not grains.
/// </summary>
public interface IMapGrain : IGrainWithStringKey
{
    Task<MapPresenceRegistration> RegisterPresenceAsync(MapPlayerPresence presence);
    Task<MapPresenceUnregistration> UnregisterPresenceAsync(uint characterId, Guid presenceId);
    Task<MapPresenceSnapshot> GetPresenceAsync();
}

[GenerateSerializer]
public sealed record MapPlayerPresence(
    [property: Id(0)] Guid PresenceId,
    [property: Id(1)] uint ActorId,
    [property: Id(2)] uint CharacterId,
    [property: Id(3)] ushort X,
    [property: Id(4)] ushort Y);

public enum MapPresenceRegistrationStatus
{
    Registered,
    AlreadyRegistered,
    Conflict,
}

[GenerateSerializer]
public sealed record MapPresenceRegistration(
    [property: Id(0)] string MapId,
    [property: Id(1)] MapPresenceRegistrationStatus Status,
    [property: Id(2)] int PresenceCount);

public enum MapPresenceUnregistrationStatus
{
    Removed,
    AlreadyAbsent,
    PresenceMismatch,
}

[GenerateSerializer]
public sealed record MapPresenceUnregistration(
    [property: Id(0)] string MapId,
    [property: Id(1)] MapPresenceUnregistrationStatus Status,
    [property: Id(2)] int PresenceCount);

[GenerateSerializer]
public sealed record MapPresenceSnapshot(
    [property: Id(0)] string MapId,
    [property: Id(1)] IReadOnlyList<MapPlayerPresence> Players);
