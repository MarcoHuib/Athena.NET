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

    public static WorldPartitionResolver CreateDevelopment(IEnumerable<string> servedMaps) => new(
        [
            new("prontera-region", ["prontera", "prt_fild*"]),
            new("world-rest", ["*"], ["prontera", "prt_fild*"]),
        ], servedMaps);
}

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

public readonly record struct WorldActorIdRange(string PartitionId, uint StartInclusive, uint EndInclusive)
{
    public void Validate() { if (StartInclusive < 110_000_000 || StartInclusive > EndInclusive) throw new InvalidOperationException($"Invalid actor-ID range for '{PartitionId}'."); }
}

public static class WorldPartitionActorRanges
{
    public static readonly IReadOnlyList<WorldActorIdRange> Development =
    [
        new("prontera-region", 110_000_000, 119_999_999),
        new("world-rest", 120_000_000, 129_999_999),
    ];

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
