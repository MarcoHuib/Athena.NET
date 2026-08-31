using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Phase-one boundary between the Ragnarok transport adapter and distributed map authority.
// The local PlayerVisibilityCoordinator remains the packet-facing AOI projection until movement
// and visibility migrate together in a later phase.
public interface IWorldRuntime
{
    Task<MapPresenceRegistration> RegisterPresenceAsync(string mapId, MapPlayerPresence presence, CancellationToken cancellationToken);
    Task<MapPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken);
}
