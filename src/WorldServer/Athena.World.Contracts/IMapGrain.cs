using Orleans;

namespace Athena.Net.World.Contracts;

/// <summary>
/// Coarse-grained authority for one logical Ragnarok map. Hot map simulation belongs inside this
/// boundary; individual monsters, cells, movement operations, and combat operations are not grains.
/// </summary>
public interface IMapGrain : IGrainWithStringKey
{
    Task<MapPresenceRegistration> RegisterPresenceAsync(MapPlayerPresence presence);
    Task<bool> UnregisterPresenceAsync(uint characterId);
    Task<MapPresenceSnapshot> GetPresenceAsync();
}

[GenerateSerializer]
public sealed record MapPlayerPresence(
    [property: Id(0)] uint ActorId,
    [property: Id(1)] uint CharacterId,
    [property: Id(2)] ushort X,
    [property: Id(3)] ushort Y);

[GenerateSerializer]
public sealed record MapPresenceRegistration(
    [property: Id(0)] string MapId,
    [property: Id(1)] bool Registered,
    [property: Id(2)] int PresenceCount);

[GenerateSerializer]
public sealed record MapPresenceSnapshot(
    [property: Id(0)] string MapId,
    [property: Id(1)] IReadOnlyList<MapPlayerPresence> Players);
