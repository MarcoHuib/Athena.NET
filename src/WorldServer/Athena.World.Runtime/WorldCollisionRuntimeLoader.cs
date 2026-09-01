using Athena.Rathena.Data;

namespace Athena.Net.World.Runtime;

public sealed record WorldCollisionRuntime(IMapCollisionProvider Collision, IMovementPathProvider Movement, int MapCount, string SourcePath);

/// <summary>Protocol-independent, fail-closed composition of the layered rAthena map-cache development override.</summary>
public static class WorldCollisionRuntimeLoader
{
    public static WorldCollisionRuntime LoadMapCache(string mapCachePath, bool renewal = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapCachePath);
        var resolvedPath = Path.GetFullPath(mapCachePath);
        if (!File.Exists(resolvedPath))
            throw new FileNotFoundException($"Required World map cache was not found at '{resolvedPath}'.", resolvedPath);

        var dbDirectory = Path.GetDirectoryName(resolvedPath)!;
        var importPath = Path.Combine(dbDirectory, "import", "map_cache.dat");
        var rulesetPath = Path.Combine(dbDirectory, renewal ? "re" : "pre-re", "map_cache.dat");
        var merged = RathenaMapCacheLayers.Merge(
            File.ReadAllBytes(resolvedPath),
            File.Exists(rulesetPath) ? File.ReadAllBytes(rulesetPath) : null,
            File.Exists(importPath) ? File.ReadAllBytes(importPath) : null);
        var maps = merged.ToDictionary(
            item => item.Entry.Name,
            item => ToCollisionMap(item.Entry),
            StringComparer.OrdinalIgnoreCase);
        if (maps.Count == 0) throw new InvalidDataException($"World map cache '{resolvedPath}' contains no maps.");
        var collision = new MapCollisionProvider(maps);
        return new(collision, new RathenaCompatibleMovementPathProvider(collision), maps.Count, resolvedPath);
    }

    private static MapCollisionMap ToCollisionMap(RathenaMapCacheFormat.Entry entry) =>
        new(entry.Name, entry.Width, entry.Height, entry.RawCells.Select((cell, index) => cell switch
        {
            0 or 2 or 4 or 6 => MapCellFlags.Walkable | MapCellFlags.Shootable,
            1 => MapCellFlags.None,
            3 => MapCellFlags.Walkable | MapCellFlags.Shootable | MapCellFlags.Water,
            5 => MapCellFlags.Shootable,
            _ => throw new InvalidDataException($"map_cache.dat map '{entry.Name}' cell {index} has unrecognized GAT type {cell}."),
        }).ToArray());
}
