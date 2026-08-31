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
        if (presence.ActorId == 0 || presence.CharacterId == 0)
            throw new ArgumentException("Player actor and character identities must be non-zero.", nameof(presence));

        var registered = _players.TryAdd(presence.CharacterId, presence);
        return Task.FromResult(new MapPresenceRegistration(this.GetPrimaryKeyString(), registered, _players.Count));
    }

    public Task<bool> UnregisterPresenceAsync(uint characterId) =>
        Task.FromResult(_players.Remove(characterId));

    public Task<MapPresenceSnapshot> GetPresenceAsync() =>
        Task.FromResult(new MapPresenceSnapshot(
            this.GetPrimaryKeyString(),
            _players.Values.OrderBy(player => player.CharacterId).ToArray()));
}
