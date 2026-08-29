namespace Athena.Net.MapServer.World;

// Thread-safe authoritative player registry with a per-map uniform-grid index.
// All index mutations happen under one short in-memory lock, making actor lookup,
// character lookup, and bucket membership change atomically with snapshot replacement.
public sealed class PlayerPresenceRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, PlayerPresence> _byActorId = [];
    private readonly Dictionary<uint, uint> _actorIdByCharacterId = [];
    private readonly Dictionary<BucketKey, HashSet<uint>> _buckets = [];
    private readonly WorldVisibilityOptions _options;

    public PlayerPresenceRegistry(WorldVisibilityOptions? options = null) =>
        _options = options ?? WorldVisibilityOptions.Default;

    public int Count { get { lock (_gate) return _byActorId.Count; } }

    public bool TryRegister(PlayerPresence presence)
    {
        Validate(presence);
        lock (_gate)
        {
            if (_byActorId.ContainsKey(presence.ActorId) || _actorIdByCharacterId.ContainsKey(presence.CharacterId)) return false;
            _byActorId.Add(presence.ActorId, presence);
            _actorIdByCharacterId.Add(presence.CharacterId, presence.ActorId);
            AddToBucket(presence);
            return true;
        }
    }

    public bool TryGetByActorId(uint actorId, out PlayerPresence presence)
    {
        lock (_gate) return _byActorId.TryGetValue(actorId, out presence!);
    }

    public bool TryGetByCharacterId(uint characterId, out PlayerPresence presence)
    {
        lock (_gate)
        {
            if (_actorIdByCharacterId.TryGetValue(characterId, out var actorId) && _byActorId.TryGetValue(actorId, out presence!)) return true;
            presence = default!;
            return false;
        }
    }

    public bool TryReplace(PlayerPresence replacement, out PlayerPresence previous)
    {
        Validate(replacement);
        lock (_gate)
        {
            if (!_byActorId.TryGetValue(replacement.ActorId, out previous!) || previous.CharacterId != replacement.CharacterId) return false;
            var oldKey = GetBucketKey(previous);
            var newKey = GetBucketKey(replacement);
            if (oldKey != newKey)
            {
                RemoveFromBucket(oldKey, previous.ActorId);
                AddToBucket(replacement);
            }
            _byActorId[replacement.ActorId] = replacement;
            return true;
        }
    }

    public bool TryUnregister(uint actorId, out PlayerPresence removed)
    {
        lock (_gate)
        {
            if (!_byActorId.Remove(actorId, out removed!)) return false;
            _actorIdByCharacterId.Remove(removed.CharacterId);
            RemoveFromBucket(GetBucketKey(removed), actorId);
            return true;
        }
    }

    public IReadOnlyList<PlayerPresence> QueryNearby(string mapName, ushort x, ushort y)
    {
        lock (_gate)
        {
            var actorIds = QueryCandidateActorIdsCore(mapName, x, y);
            var result = new List<PlayerPresence>(actorIds.Count);
            foreach (var actorId in actorIds)
            {
                var presence = _byActorId[actorId];
                if (_options.IsVisible(mapName, x, y, presence.MapName, presence.X, presence.Y)) result.Add(presence);
            }
            return result;
        }
    }

    internal IReadOnlySet<uint> QueryCandidateActorIds(string mapName, ushort x, ushort y)
    {
        lock (_gate) return QueryCandidateActorIdsCore(mapName, x, y);
    }

    private HashSet<uint> QueryCandidateActorIdsCore(string mapName, ushort x, ushort y)
    {
        var result = new HashSet<uint>();
        var bucketSize = _options.BucketSize;
        var bucketRadius = (_options.AreaSize + bucketSize - 1) / bucketSize + 1;
        var centerX = x / bucketSize;
        var centerY = y / bucketSize;
        for (var bx = Math.Max(0, centerX - bucketRadius); bx <= centerX + bucketRadius; bx++)
        for (var by = Math.Max(0, centerY - bucketRadius); by <= centerY + bucketRadius; by++)
        {
            if (_buckets.TryGetValue(new BucketKey(mapName, bx, by), out var bucket)) result.UnionWith(bucket);
        }
        return result;
    }

    private void AddToBucket(PlayerPresence presence)
    {
        var key = GetBucketKey(presence);
        if (!_buckets.TryGetValue(key, out var bucket)) _buckets.Add(key, bucket = []);
        if (!bucket.Add(presence.ActorId)) throw new InvalidOperationException($"Duplicate player bucket membership for actor {presence.ActorId}.");
    }

    private void RemoveFromBucket(BucketKey key, uint actorId)
    {
        if (!_buckets.TryGetValue(key, out var bucket) || !bucket.Remove(actorId))
            throw new InvalidOperationException($"Missing player bucket membership for actor {actorId}.");
        if (bucket.Count == 0) _buckets.Remove(key);
    }

    private BucketKey GetBucketKey(PlayerPresence presence) =>
        new(presence.MapName, presence.X / _options.BucketSize, presence.Y / _options.BucketSize);

    private static void Validate(PlayerPresence presence)
    {
        if (presence.ActorId == 0 || presence.CharacterId == 0) throw new ArgumentException("Player identity must be non-zero.", nameof(presence));
        if (presence.ActorId >= 110_000_000) throw new ArgumentException("Player actor IDs must remain outside the NPC/monster allocator domain.", nameof(presence));
        if (string.IsNullOrWhiteSpace(presence.MapName) || string.IsNullOrWhiteSpace(presence.CharacterName)) throw new ArgumentException("Player map and name are required.", nameof(presence));
    }

    private readonly record struct BucketKey
    {
        public BucketKey(string mapName, int x, int y)
        {
            MapName = mapName.ToUpperInvariant();
            X = x;
            Y = y;
        }
        public string MapName { get; }
        public int X { get; }
        public int Y { get; }
    }
}
