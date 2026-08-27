using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Logging;

namespace Athena.Net.MapServer.World;

// Turns configured collision-data settings into a composed IMapCollisionProvider at MapServer
// startup. This is the ONLY place real collision data is read from disk - MapClientSession never
// opens files, matching this project's existing "MapServerApp/MapServerWorld composition owns
// loading, MapClientSession only consumes already-composed state" convention (see
// MapServerWorld.Build's own doc comment for the analogous GameplayRuleServices composition
// boundary). Reading/decompression happens exactly once here, at startup; the resulting
// MapCollisionMap instances are immutable and shared for the server's lifetime - nothing re-reads
// or re-decompresses per session or per lookup.
//
// Two mutually exclusive sources (MapConfigLoader already rejects configuring both):
//   - map_cache_path: the NORMAL source (ai/world-data.md) - pinned rAthena's own db/map_cache.dat,
//     read whole via RathenaMapCacheReader, one MapCollisionMap per map the file declares.
//   - map_collision_artifact (one or more): SECONDARY/debug tooling - locally supplied .gat-derived
//     Athena artifacts (MapCollisionArtifact/MapCollisionCompiler), registered under explicit
//     logical map name aliases.
// Configuring neither preserves the original default: EmptyMapCollisionProvider.Instance.
public static class MapCollisionStartupLoader
{
    public static IMapCollisionProvider Load(IReadOnlyList<MapCollisionArtifactConfig> artifacts, string? mapCachePath = null)
    {
        if (mapCachePath is { Length: > 0 })
        {
            return LoadFromMapCache(mapCachePath);
        }

        if (artifacts.Count == 0)
        {
            return EmptyMapCollisionProvider.Instance;
        }

        return LoadFromArtifacts(artifacts);
    }

    // Loads every map declared in a pinned map_cache.dat in one pass. A missing/malformed file
    // fails startup loudly (no silent fallback to EmptyMapCollisionProvider) - an operator who
    // configured map_cache_path must be told if it did not load, not left believing it did.
    // map_cache.dat's own map names are used verbatim as Athena's logical map names: each pinned
    // map (including int_land/int_land01../04, which the file itself declares as distinct records
    // with real geometry) is registered under its own name, with no alias mechanism - see
    // ai/world-data.md for why an alias layer is not needed for this source.
    private static IMapCollisionProvider LoadFromMapCache(string mapCachePath)
    {
        if (!File.Exists(mapCachePath))
        {
            throw new InvalidOperationException($"Configured map_cache_path '{mapCachePath}' was not found.");
        }

        IReadOnlyList<MapCollisionMap> maps;
        try
        {
            maps = RathenaMapCacheReader.ReadAllFromFile(mapCachePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new InvalidOperationException($"Configured map_cache_path '{mapCachePath}' could not be read: {ex.Message}", ex);
        }

        var byMapName = new Dictionary<string, MapCollisionMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in maps)
        {
            if (!byMapName.TryAdd(map.MapName, map))
            {
                throw new InvalidOperationException($"map_cache.dat contains duplicate map name '{map.MapName}'.");
            }
        }

        MapLogger.Status($"Loaded map_cache.dat '{mapCachePath}': {byMapName.Count} maps.");
        return new MapCollisionProvider(byMapName);
    }

    // A configured artifact that fails to load (missing file, malformed bytes, duplicate logical
    // map name across artifacts) throws rather than silently falling back - an operator who
    // configured collision data must be told loudly if it did not load, not left believing it did.
    private static IMapCollisionProvider LoadFromArtifacts(IReadOnlyList<MapCollisionArtifactConfig> artifacts)
    {
        var byMapName = new Dictionary<string, MapCollisionMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            if (!File.Exists(artifact.Path))
            {
                throw new InvalidOperationException(
                    $"Configured map collision artifact '{artifact.Path}' was not found.");
            }

            MapCollisionMap map;
            try
            {
                map = MapCollisionArtifact.ReadFile(artifact.Path);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                throw new InvalidOperationException(
                    $"Configured map collision artifact '{artifact.Path}' could not be read: {ex.Message}", ex);
            }

            // The SAME loaded MapCollisionMap instance (and therefore the same underlying cell
            // array) is registered under every configured logical alias - one artifact load never
            // duplicates cell storage per alias.
            foreach (var logicalMapName in artifact.Maps)
            {
                if (!byMapName.TryAdd(logicalMapName, map))
                {
                    throw new InvalidOperationException(
                        $"Map collision artifact configuration registers logical map '{logicalMapName}' more than once.");
                }
            }

            MapLogger.Status(
                $"Loaded collision map resource '{artifact.Path}' dimensions={map.Width}x{map.Height} logical aliases={string.Join(",", artifact.Maps)}");
        }

        return new MapCollisionProvider(byMapName);
    }
}
