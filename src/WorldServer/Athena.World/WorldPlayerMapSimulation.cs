using Athena.Net.World.Contracts;

namespace Athena.Net.World;

// Item 14: one map's worth of World-owned player presence/event feed state, owned entirely by
// WorldPartitionGrain (never its own grain - same "one coarse WorldPartitionGrain" architecture as
// WorldMonsterMapSimulation). Mirrors that type's proven feed shape (per-map epoch, monotonic
// sequence, bounded retention, atomic snapshot bootstrap, resync-on-stale-cursor) applied to
// players instead of monsters - see WorldMonsterMapSimulation's own doc comment for the pattern
// this deliberately reuses rather than reinventing.
//
// This is NOT a second player-presence authority: WorldPartitionGrain's own _maps (MapRuntime.Players,
// Dictionary<uint CharacterId, WorldPlayerPresence>) remains the sole position/identity/life-state
// authority. This type owns only (a) the feed itself (append-only entries + cursor/epoch machinery)
// and (b) the purely-cosmetic WorldPlayerPublicState per character, which has no gameplay effect and
// exists solely so a remote MapServer replica can construct the existing player packets (item 14 §1).
internal sealed class WorldPlayerMapSimulation
{
    private readonly List<WorldPlayerFeedEntry> _entries = [];
    private long _nextSequence = 1;
    private readonly Dictionary<uint, WorldPlayerPublicState> _publicStateByCharacterId = [];

    public string MapId { get; }
    public WorldSimulationEpoch Epoch { get; private set; }

    public WorldPlayerMapSimulation(string mapId)
    {
        MapId = mapId;
        Epoch = WorldSimulationEpoch.NewEpoch();
    }

    public bool TryGetPublicState(uint characterId, out WorldPlayerPublicState publicState) =>
        _publicStateByCharacterId.TryGetValue(characterId, out publicState!);

    public void SetPublicState(uint characterId, WorldPlayerPublicState publicState) =>
        _publicStateByCharacterId[characterId] = publicState;

    public void RemovePublicState(uint characterId) => _publicStateByCharacterId.Remove(characterId);

    public long AsOfSequence => _nextSequence - 1;

    // Same bounded-retention discipline as WorldMonsterMapSimulation.Append (4096 entries, trimmed
    // from the front) - a consumer that falls further behind than this receives ResyncRequired
    // (BuildPage), never a silently-truncated gap.
    public void Append(WorldPlayerFeedEntryKind kind, WorldPlayerPresenceEntry entry)
    {
        var feedEntry = new WorldPlayerFeedEntry(_nextSequence++, kind, entry.Presence.ActorId, entry.Presence.CharacterId, entry);
        _entries.Add(feedEntry);
        const int retention = 4096;
        if (_entries.Count > retention) _entries.RemoveRange(0, _entries.Count - retention);
    }

    // Identical control flow to WorldMonsterMapSimulation.BuildPage: null cursor -> atomic bootstrap
    // (caller supplies the current snapshot); epoch mismatch or sequence outside the retention
    // window -> resync (fresh snapshot); otherwise -> incremental entries filtered to > cursor.Sequence.
    public WorldPlayerFeedPage BuildPage(WorldPlayerFeedCursor? cursor, IReadOnlyList<WorldPlayerPresenceEntry> currentSnapshot)
    {
        if (cursor is null)
            return new WorldPlayerFeedPage(MapId, Epoch, WorldPlayerFeedStatus.Ready, currentSnapshot, Entries: null, AsOfSequence);
        if (!cursor.Value.Epoch.Equals(Epoch))
            return new WorldPlayerFeedPage(MapId, Epoch, WorldPlayerFeedStatus.ResyncRequired, currentSnapshot, Entries: null, AsOfSequence);
        var oldestRetained = _entries.Count > 0 ? _entries[0].Sequence : _nextSequence;
        if (cursor.Value.Sequence < oldestRetained - 1 || cursor.Value.Sequence > AsOfSequence)
            return new WorldPlayerFeedPage(MapId, Epoch, WorldPlayerFeedStatus.ResyncRequired, currentSnapshot, Entries: null, AsOfSequence);
        var incremental = _entries.Where(e => e.Sequence > cursor.Value.Sequence).ToArray();
        return new WorldPlayerFeedPage(MapId, Epoch, WorldPlayerFeedStatus.Ready, Snapshot: null, incremental, AsOfSequence);
    }
}
