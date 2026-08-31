using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Phase-one boundary between the Ragnarok transport adapter and distributed map authority.
// The local PlayerVisibilityCoordinator remains the packet-facing AOI projection until movement
// and visibility migrate together in a later phase.
public interface IWorldRuntime
{
    Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken);
    Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken);
    Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken);
    Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken);
}
