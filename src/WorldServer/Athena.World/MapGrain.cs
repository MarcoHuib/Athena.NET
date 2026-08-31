using Athena.Net.World.Contracts;
using Athena.Net.World.Telemetry;
using Orleans;

namespace Athena.Net.World;

public sealed class MapGrain : Grain, IMapGrain
{
    private readonly Dictionary<uint, MapPlayerPresence> _players = [];

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        WorldTelemetry.MapActivations.Add(1, new KeyValuePair<string, object?>("world.map.id", this.GetPrimaryKeyString()));
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<MapPresenceRegistration> RegisterPresenceAsync(MapPlayerPresence presence)
    {
        Validate(presence);
        var mapId = this.GetPrimaryKeyString();
        if (!_players.TryGetValue(presence.CharacterId, out var existing))
        {
            _players.Add(presence.CharacterId, presence);
            return Task.FromResult(new MapPresenceRegistration(mapId, MapPresenceRegistrationStatus.Registered, _players.Count));
        }

        if (existing.PresenceId != presence.PresenceId)
            return Task.FromResult(new MapPresenceRegistration(mapId, MapPresenceRegistrationStatus.Conflict, _players.Count));

        // A caller can replay after the first execution committed but its response was lost. The
        // immutable snapshot may be refreshed for that same logical lifecycle without duplicating it.
        _players[presence.CharacterId] = presence;
        return Task.FromResult(new MapPresenceRegistration(mapId, MapPresenceRegistrationStatus.AlreadyRegistered, _players.Count));
    }

    public Task<MapPresenceUnregistration> UnregisterPresenceAsync(uint characterId, Guid presenceId)
    {
        if (characterId == 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (presenceId == Guid.Empty) throw new ArgumentException("Presence identity must be non-empty.", nameof(presenceId));

        var mapId = this.GetPrimaryKeyString();
        if (!_players.TryGetValue(characterId, out var existing))
            return Task.FromResult(new MapPresenceUnregistration(mapId, MapPresenceUnregistrationStatus.AlreadyAbsent, _players.Count));
        if (existing.PresenceId != presenceId)
            return Task.FromResult(new MapPresenceUnregistration(mapId, MapPresenceUnregistrationStatus.PresenceMismatch, _players.Count));

        _players.Remove(characterId);
        return Task.FromResult(new MapPresenceUnregistration(mapId, MapPresenceUnregistrationStatus.Removed, _players.Count));
    }

    public Task<MapPresenceSnapshot> GetPresenceAsync() =>
        Task.FromResult(new MapPresenceSnapshot(
            this.GetPrimaryKeyString(),
            _players.Values.OrderBy(player => player.CharacterId).ToArray()));

    private static void Validate(MapPlayerPresence presence)
    {
        if (presence.PresenceId == Guid.Empty)
            throw new ArgumentException("Presence identity must be non-empty.", nameof(presence));
        if (presence.ActorId == 0 || presence.CharacterId == 0)
            throw new ArgumentException("Player actor and character identities must be non-zero.", nameof(presence));
    }
}
