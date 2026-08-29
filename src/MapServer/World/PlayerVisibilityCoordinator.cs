using System.Collections.Concurrent;

namespace Athena.Net.MapServer.World;

public enum PlayerEntryKind
{
    ExistingStandingOrWalking,
    NewlySpawned,
}

// Transport-facing boundary. Implementations own per-client visible-actor edge
// tracking and packet delivery; the coordinator owns registry/AOI rules.
public interface IPlayerPresenceObserver
{
    uint ActorId { get; }
    Task PlayerEnteredViewAsync(PlayerPresence presence, PlayerEntryKind kind, CancellationToken cancellationToken);
    Task PlayerMovementChangedAsync(PlayerPresence presence, CancellationToken cancellationToken);
    Task PlayerLookChangedAsync(PlayerPresence presence, CancellationToken cancellationToken);
    Task PlayerLeftViewAsync(uint actorId, CancellationToken cancellationToken);
    void ForgetPlayer(uint actorId);
}

public sealed class PlayerVisibilityCoordinator
{
    private readonly PlayerPresenceRegistry _players;
    private readonly WorldVisibilityOptions _options;
    private readonly ConcurrentDictionary<uint, IPlayerPresenceObserver> _observers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlayerVisibilityCoordinator(PlayerPresenceRegistry players, WorldVisibilityOptions? options = null)
    {
        _players = players;
        _options = options ?? WorldVisibilityOptions.Default;
    }

    public async Task RegisterAsync(PlayerPresence presence, IPlayerPresenceObserver observer, CancellationToken cancellationToken)
    {
        if (observer.ActorId != presence.ActorId) throw new ArgumentException("Observer identity does not match presence.", nameof(observer));
        List<Task> deliveries = [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_players.TryRegister(presence)) throw new InvalidOperationException($"Player actor {presence.ActorId} is already registered.");
            if (!_observers.TryAdd(presence.ActorId, observer))
            {
                _players.TryUnregister(presence.ActorId, out _);
                throw new InvalidOperationException($"Player observer {presence.ActorId} is already registered.");
            }

            foreach (var existing in _players.QueryNearby(presence.MapName, presence.X, presence.Y))
            {
                if (existing.ActorId == presence.ActorId || !_observers.TryGetValue(existing.ActorId, out var existingObserver)) continue;
                // Pinned clif_parse_LoadEndAck: the newcomer discovers actors already in
                // area through clif_getareachar_unit (0x09FF/0x09FD); existing viewers
                // receive clif_spawn's 0x09FE for the newly-added PC.
                deliveries.Add(observer.PlayerEnteredViewAsync(existing, PlayerEntryKind.ExistingStandingOrWalking, cancellationToken));
                deliveries.Add(existingObserver.PlayerEnteredViewAsync(presence, PlayerEntryKind.NewlySpawned, cancellationToken));
            }
        }
        finally { _gate.Release(); }
        await DeliverAllAsync(deliveries);
    }

    public async Task<bool> UpdateMovementAsync(PlayerPresence replacement, bool broadcastMovement, CancellationToken cancellationToken)
    {
        List<Task> deliveries = [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_players.TryReplace(replacement, out var previous)) return false;
            if (!_observers.TryGetValue(replacement.ActorId, out var moverObserver)) return false;

            // Plain dedup loop instead of Concat/GroupBy/Select: this runs on every player
            // movement cell, and the two candidate lists overlap heavily whenever a walk stays
            // within one AOI neighborhood. Querying the destination side FIRST and letting
            // HashSet.Add's "already present" result skip the origin-side duplicate reproduces the
            // prior GroupBy(...).Select(group => group.Last()) behavior (previous+replacement
            // concatenated in that order, last-wins) without allocating a lookup/grouping.
            var seen = new HashSet<uint>();
            var affected = new List<PlayerPresence>();
            foreach (var candidate in _players.QueryNearby(replacement.MapName, replacement.X, replacement.Y))
            {
                if (seen.Add(candidate.ActorId)) affected.Add(candidate);
            }
            foreach (var candidate in _players.QueryNearby(previous.MapName, previous.X, previous.Y))
            {
                if (seen.Add(candidate.ActorId)) affected.Add(candidate);
            }

            foreach (var other in affected)
            {
                if (other.ActorId == replacement.ActorId || !_observers.TryGetValue(other.ActorId, out var otherObserver)) continue;
                var wasVisible = _options.IsVisible(previous.MapName, previous.X, previous.Y, other.MapName, other.X, other.Y);
                var isVisible = _options.IsVisible(replacement.MapName, replacement.X, replacement.Y, other.MapName, other.X, other.Y);
                if (!wasVisible && isVisible)
                {
                    deliveries.Add(otherObserver.PlayerEnteredViewAsync(replacement, PlayerEntryKind.ExistingStandingOrWalking, cancellationToken));
                    deliveries.Add(moverObserver.PlayerEnteredViewAsync(other, PlayerEntryKind.ExistingStandingOrWalking, cancellationToken));
                }
                else if (wasVisible && !isVisible)
                {
                    deliveries.Add(otherObserver.PlayerLeftViewAsync(replacement.ActorId, cancellationToken));
                    deliveries.Add(moverObserver.PlayerLeftViewAsync(other.ActorId, cancellationToken));
                }
                else if (isVisible && broadcastMovement)
                {
                    deliveries.Add(otherObserver.PlayerMovementChangedAsync(replacement, cancellationToken));
                }
            }
        }
        finally { _gate.Release(); }
        await DeliverAllAsync(deliveries);
        return true;
    }

    public async Task<bool> UpdateLookAsync(PlayerPresence replacement, CancellationToken cancellationToken)
    {
        List<Task> deliveries = [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_players.TryReplace(replacement, out _)) return false;
            foreach (var other in _players.QueryNearby(replacement.MapName, replacement.X, replacement.Y))
            {
                if (other.ActorId == replacement.ActorId || !_observers.TryGetValue(other.ActorId, out var observer)) continue;
                deliveries.Add(observer.PlayerLookChangedAsync(replacement, cancellationToken));
            }
        }
        finally { _gate.Release(); }
        await DeliverAllAsync(deliveries);
        return true;
    }

    public async Task<bool> ReplacePublicStateAsync(PlayerPresence replacement, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _players.TryReplace(replacement, out _); }
        finally { _gate.Release(); }
    }

    public async Task<bool> UnregisterAsync(uint actorId, CancellationToken cancellationToken)
    {
        List<Task> deliveries = [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_players.TryUnregister(actorId, out var removed)) return false;
            _observers.TryRemove(actorId, out var removedObserver);
            foreach (var other in _players.QueryNearby(removed.MapName, removed.X, removed.Y))
            {
                if (!_observers.TryGetValue(other.ActorId, out var observer)) continue;
                deliveries.Add(observer.PlayerLeftViewAsync(actorId, cancellationToken));
                removedObserver?.ForgetPlayer(other.ActorId);
            }
        }
        finally { _gate.Release(); }
        await DeliverAllAsync(deliveries);
        return true;
    }

    private static async Task DeliverAllAsync(IEnumerable<Task> deliveries)
    {
        foreach (var delivery in deliveries)
        {
            try { await delivery; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }
}
