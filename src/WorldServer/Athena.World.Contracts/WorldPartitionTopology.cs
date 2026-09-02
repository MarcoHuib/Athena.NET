namespace Athena.Net.World.Contracts;

using System.Text.Json;

public static class WorldMapId
{
    public static string Normalize(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        var value = mapId.EndsWith(".gat", StringComparison.OrdinalIgnoreCase) ? mapId[..^4] : mapId;
        return value.ToLowerInvariant();
    }
}

public sealed record WorldPartitionDefinition(string PartitionId, IReadOnlyList<string> IncludeMaps, IReadOnlyList<string>? ExcludeMaps = null, WorldActorIdRange? ActorIdRange = null);

public interface IWorldPartitionResolver
{
    string ResolvePartition(string mapId);
}

// Generic partition-ownership resolver: normalizes map IDs, matches include/exclude glob
// patterns, resolves exactly one owning partition, and validates topology invariants (no
// duplicate partition IDs, every served map has exactly one owner). Deliberately contains NO
// knowledge of any concrete Ragnarok map or deployment topology (no "prontera", "prt_fild*",
// "izlude", etc.) - which maps exist and how they're grouped into partitions is configuration/
// deployment policy, supplied entirely via the `definitions` constructor argument. This type also
// has no concept of WHERE that configuration lives on disk (repository layout, container
// filesystem, etc.) - see WorldPartitionTopologyLoader's own doc comment for where that
// responsibility lives instead. Both production and development source their policy from the same
// world_partitions.json shape via WorldPartitionTopologyLoader.Load, just pointed at different
// absolute paths supplied by their own composition root.
public sealed class WorldPartitionResolver : IWorldPartitionResolver
{
    private readonly IReadOnlyList<WorldPartitionDefinition> _definitions;

    public WorldPartitionResolver(IEnumerable<WorldPartitionDefinition> definitions, IEnumerable<string> servedMaps)
    {
        _definitions = definitions.ToArray();
        if (_definitions.Count == 0) throw new InvalidOperationException("At least one world partition is required.");
        var duplicateIds = _definitions.GroupBy(x => x.PartitionId, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        if (duplicateIds.Length > 0) throw new InvalidOperationException($"Duplicate world partition IDs: {string.Join(", ", duplicateIds)}.");
        foreach (var map in servedMaps) _ = ResolvePartition(map);
    }

    public string ResolvePartition(string mapId)
    {
        var normalized = WorldMapId.Normalize(mapId);
        var matches = _definitions.Where(definition => Matches(definition, normalized)).Select(x => x.PartitionId).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Served map '{normalized}' has no world partition owner."),
            _ => throw new InvalidOperationException($"Served map '{normalized}' has ambiguous world partition ownership: {string.Join(", ", matches)}."),
        };
    }

    private static bool Matches(WorldPartitionDefinition definition, string mapId) =>
        definition.IncludeMaps.Any(pattern => Glob(pattern, mapId)) &&
        !(definition.ExcludeMaps?.Any(pattern => Glob(pattern, mapId)) ?? false);

    private static bool Glob(string pattern, string mapId)
    {
        var normalized = WorldMapId.Normalize(pattern);
        if (normalized == "*") return true;
        return normalized.EndsWith('*')
            ? mapId.StartsWith(normalized[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(normalized, mapId, StringComparison.OrdinalIgnoreCase);
    }
}

// Loads a WorldPartitionResolver from a world_partitions.json-shaped file at an EXPLICIT,
// already-resolved absolute (or working-directory-relative) `path`. This type never searches
// parent directories, never assumes a source-repository layout, and never knows about a solution
// file - resolving WHERE the topology file lives (a checked-out repo's conf/ directory in
// development, /etc/athena/world_partitions.json in a container, an env-var override, etc.) is
// entirely the caller's/composition-root's responsibility (see MapServerApp.RunAsync and
// Athena.World's own Program.cs, both of which already read the ATHENA_WORLD_PARTITIONS_PATH
// environment variable for exactly this purpose - reuse that convention rather than inventing a
// second path-discovery mechanism).
//
// The document is a flat JSON object whose keys are EITHER a partition ID (an object with
// includeMaps/excludeMaps/actorIdRange) OR the one reserved sibling key "npcWarpActorIdRange" (an
// {start,end} object reserving the actor-ID sub-range MapServer's own still-local NPC/warp
// WorldActorIdAllocator draws from - see WorldPartitionTopologyDocument.NpcWarpActorIdRange's own
// doc comment for why this single config file is the one source of truth for the ENTIRE global
// 110,000,000+ actor-ID namespace, not just the per-partition monster sub-ranges). A partition's
// own `actorIdRange` is optional in the JSON only for older/test fixtures that predate this field;
// Load itself does not require every partition to declare one - callers that need actor-ID
// allocation (Athena.World's grain composition) validate that separately via
// WorldPartitionActorRanges.ValidateAll, which also cross-checks the reserved NPC/warp range.
public static class WorldPartitionTopologyLoader
{
    public static WorldPartitionResolver Load(string path, IEnumerable<string> servedMaps)
    {
        var document = LoadDocument(path);
        return new WorldPartitionResolver(document.Partitions, servedMaps);
    }

    // Loads the complete parsed document (partition definitions AND the reserved NPC/warp actor-ID
    // range) - the entry point for a composition root (Athena.World's Program.cs,
    // MapServerApp.RunAsync) that needs BOTH the resolver-feeding partition list and the NPC/warp
    // range together, without parsing the same file twice through two different code paths.
    public static WorldPartitionTopologyDocument LoadDocument(string path)
    {
        using var stream = File.OpenRead(path);
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"World partition topology '{path}' is empty.");

        WorldActorIdRange? npcWarpRange = null;
        var partitions = new List<WorldPartitionDefinition>();
        foreach (var (key, value) in raw)
        {
            if (string.Equals(key, "npcWarpActorIdRange", StringComparison.OrdinalIgnoreCase))
            {
                var rangeJson = value.Deserialize<ActorIdRangeJson>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException($"World partition topology '{path}' has an invalid 'npcWarpActorIdRange'.");
                npcWarpRange = new WorldActorIdRange("npc-warp", rangeJson.Start, rangeJson.End);
                continue;
            }
            var partitionJson = value.Deserialize<PartitionJson>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException($"World partition topology '{path}' has an invalid entry for '{key}'.");
            var actorIdRange = partitionJson.ActorIdRange is { } r ? new WorldActorIdRange(key, r.Start, r.End) : (WorldActorIdRange?)null;
            partitions.Add(new WorldPartitionDefinition(key, partitionJson.IncludeMaps ?? [], partitionJson.ExcludeMaps, actorIdRange));
        }
        if (partitions.Count == 0) throw new InvalidOperationException($"World partition topology '{path}' declares no partitions.");
        return new WorldPartitionTopologyDocument(partitions, npcWarpRange);
    }

    private sealed record PartitionJson(string[]? IncludeMaps, string[]? ExcludeMaps, ActorIdRangeJson? ActorIdRange);
    private sealed record ActorIdRangeJson(uint Start, uint End);
}

// The complete parsed contents of a world_partitions.json-shaped file: every partition definition
// (feeding WorldPartitionResolver) plus the one reserved NpcWarpActorIdRange sibling entry. Kept as
// its own record (rather than a bare tuple) since both fields are meaningful named config, and a
// composition root generally needs both from a single Load call.
public sealed record WorldPartitionTopologyDocument(IReadOnlyList<WorldPartitionDefinition> Partitions, WorldActorIdRange? NpcWarpActorIdRange);

public readonly record struct WorldActorIdRange(string PartitionId, uint StartInclusive, uint EndInclusive)
{
    public void Validate() { if (StartInclusive < 110_000_000 || StartInclusive > EndInclusive) throw new InvalidOperationException($"Invalid actor-ID range for '{PartitionId}'."); }
}

// Config-driven actor-ID range validation - replaces the earlier hardcoded WorldPartitionActorRanges.
// Development table, which was structurally disconnected from conf/world_partitions.json's actual
// partition definitions and would silently desync if a partition were ever added/renamed/split.
// ValidateAll takes the SAME WorldPartitionTopologyDocument a composition root already loaded via
// WorldPartitionTopologyLoader.LoadDocument, so there is exactly one config source of truth for
// both map ownership (WorldPartitionResolver) and the entire global 110,000,000+ actor-ID
// namespace (every partition's monster range AND the reserved NPC/warp range together) - never two
// independently-maintained range tables that could overlap without either one knowing.
public static class WorldPartitionActorRanges
{
    public static void ValidateAll(WorldPartitionTopologyDocument document)
    {
        var ranges = document.Partitions
            .Where(p => p.ActorIdRange is not null)
            .Select(p => p.ActorIdRange!.Value)
            .ToList();
        if (document.NpcWarpActorIdRange is { } npcWarp) ranges.Add(npcWarp);
        Validate(ranges);
    }

    public static void Validate(IReadOnlyList<WorldActorIdRange> ranges)
    {
        foreach (var range in ranges) range.Validate();
        for (var i = 0; i < ranges.Count; i++)
        for (var j = i + 1; j < ranges.Count; j++)
            if (ranges[i].StartInclusive <= ranges[j].EndInclusive && ranges[j].StartInclusive <= ranges[i].EndInclusive)
                throw new InvalidOperationException($"Actor-ID ranges for '{ranges[i].PartitionId}' and '{ranges[j].PartitionId}' overlap.");
    }
}

public sealed class PartitionWorldActorIdAllocator
{
    private readonly WorldActorIdRange _range;
    private long _last;
    public PartitionWorldActorIdAllocator(WorldActorIdRange range) { range.Validate(); _range = range; _last = range.StartInclusive - 1L; }
    public uint Allocate()
    {
        var value = Interlocked.Increment(ref _last);
        if (value > _range.EndInclusive) throw new InvalidOperationException($"Actor-ID range for '{_range.PartitionId}' is exhausted.");
        return (uint)value;
    }
}
