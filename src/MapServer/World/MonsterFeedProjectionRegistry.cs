using System.Collections.Concurrent;

namespace Athena.Net.MapServer.World;

// Owns exactly one MonsterFeedProjection per currently-active map (a map with at least one
// connected MapServer session) - "only poll maps which currently have active MapServer sessions".
// Retaining a projection's cursor/snapshot/combat-state entries when its map temporarily has zero
// sessions is EXPLICITLY allowed (a session return continues from the cursor, or follows the
// explicit resync flow if the cursor has gone stale) - this registry therefore never eagerly
// removes a projection merely because a map's session count momentarily dropped to zero; only an
// explicit Evict call (driven by the owner's own idle-map policy, if one is ever added) removes one.
public sealed class MonsterFeedProjectionRegistry
{
    private readonly ConcurrentDictionary<string, MonsterFeedProjection> _byMapId = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string mapId, out MonsterFeedProjection projection) => _byMapId.TryGetValue(mapId, out projection!);

    // Lazily creates a projection the FIRST time a map is genuinely touched (a session connects to
    // it) - never pre-created for every map this process could theoretically serve, matching this
    // project's established "never pre-materialize state for a map nobody has touched" convention.
    public MonsterFeedProjection GetOrCreate(string mapId) => _byMapId.GetOrAdd(mapId, static id => new MonsterFeedProjection(id));

    // The set of maps this registry currently tracks a projection for - used by the polling loop to
    // decide which maps to call PollMonsterFeedAsync against (only maps with active sessions - see
    // this type's own top-of-file doc comment for why a projection can still exist here with zero
    // CURRENT sessions without being polled; the poller itself cross-references active session maps).
    public IReadOnlyCollection<string> TrackedMapIds => (IReadOnlyCollection<string>)_byMapId.Keys;
}
