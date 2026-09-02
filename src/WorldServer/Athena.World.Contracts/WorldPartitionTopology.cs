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

public sealed record WorldPartitionDefinition(string PartitionId, IReadOnlyList<string> IncludeMaps, IReadOnlyList<string>? ExcludeMaps = null);

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
//
// Deliberately carries no actor-ID concept at all: partition topology and actor-ID capacity
// planning are fully independent concerns - see ActorIdBlockAuthority.cs's own doc comment for
// where global actor-ID uniqueness is actually guaranteed (a single leased-block Orleans grain,
// never a config-declared numeric range tied to a specific partition).
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
public static class WorldPartitionTopologyLoader
{
    public static WorldPartitionResolver Load(string path, IEnumerable<string> servedMaps)
    {
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<Dictionary<string, PartitionJson>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"World partition topology '{path}' is empty.");
        return new WorldPartitionResolver(document.Select(entry => new WorldPartitionDefinition(
            entry.Key,
            entry.Value.IncludeMaps ?? [],
            entry.Value.ExcludeMaps)), servedMaps);
    }

    private sealed record PartitionJson(string[]? IncludeMaps, string[]? ExcludeMaps);
}
