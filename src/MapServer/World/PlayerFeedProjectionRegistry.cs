using System.Collections.Concurrent;

namespace Athena.Net.MapServer.World;

// Item 14 §4: owns exactly one PlayerFeedProjection per currently-active map, mirroring
// MonsterFeedProjectionRegistry exactly (see that type's own doc comment for the "never eagerly
// remove a projection merely because session count momentarily dropped to zero" policy this reuses).
public sealed class PlayerFeedProjectionRegistry
{
    private readonly ConcurrentDictionary<string, PlayerFeedProjection> _byMapId = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string mapId, out PlayerFeedProjection projection) => _byMapId.TryGetValue(mapId, out projection!);

    public PlayerFeedProjection GetOrCreate(string mapId) => _byMapId.GetOrAdd(mapId, static id => new PlayerFeedProjection(id));

    public IReadOnlyCollection<string> TrackedMapIds => (IReadOnlyCollection<string>)_byMapId.Keys;
}
