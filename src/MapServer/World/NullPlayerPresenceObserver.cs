namespace Athena.Net.MapServer.World;

// Item 14 §4: PlayerVisibilityCoordinator.RegisterAsync requires a live IPlayerPresenceObserver for
// every registered presence, including a REMOTE player (one connected to a different MapServer
// replica, discovered only through the player feed - see PlayerFeedProjection). A remote player has
// no local socket to deliver packets to, so it is registered with this no-op observer: it still
// participates fully in AOI math (registry/bucket membership, enter/leave/movement/look decisions)
// against LOCAL sessions' own observers, it just never itself receives a delivery (there is nowhere
// for one to go - MapTcpServer never holds a MapClientSession for a remote player).
public sealed class NullPlayerPresenceObserver(uint actorId) : IPlayerPresenceObserver
{
    public uint ActorId { get; } = actorId;
    public Task PlayerEnteredViewAsync(PlayerPresence presence, PlayerEntryKind kind, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task PlayerMovementChangedAsync(PlayerPresence presence, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task PlayerLookChangedAsync(PlayerPresence presence, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task PlayerLeftViewAsync(uint actorId, CancellationToken cancellationToken) => Task.CompletedTask;
    public void ForgetPlayer(uint actorId) { }
}
