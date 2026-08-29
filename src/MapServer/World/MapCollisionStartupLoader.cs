using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;
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
    public static IMapCollisionProvider Load(IReadOnlyList<MapCollisionArtifactConfig> artifacts, string? mapCachePath = null, RagnarokRuleSet ruleSet = RagnarokRuleSet.Renewal)
    {
        if (mapCachePath is { Length: > 0 })
        {
            return LoadFromMapCache(mapCachePath, ruleSet);
        }

        if (artifacts.Count == 0)
        {
            return EmptyMapCollisionProvider.Instance;
        }

        return LoadFromArtifacts(artifacts);
    }

    // Loads pinned map_cache.dat data, merging THREE layers in the SAME first-match-wins priority
    // order pinned rAthena's own map_readallmaps (map.cpp:3908-3943) uses, from highest to lowest
    // priority: db/import/map_cache.dat, then the RULESET-SPECIFIC cache (db/re/map_cache.dat for
    // Renewal, db/pre-re/map_cache.dat for PreRenewal - src/config/const.hpp's own DBPATH macro),
    // then the configured `mapCachePath` (Athena's own generic/broad db/map_cache.dat) as the
    // final fallback for any map neither higher layer declares. The ruleset-specific layer is a
    // real, non-cosmetic distinction: pinned db/re/map_cache.dat is a small, CURATED set of maps
    // whose Renewal geometry genuinely differs from the generic/legacy cache (independently
    // confirmed: "prontera" exists ONLY in db/re/map_cache.dat at 312x392, not at all in the root
    // db/map_cache.dat, while db/re/map_cache.dat itself has only 8 total maps and does not
    // declare izlude_d/prt_fild08d/int_land04/etc. at all) - a live Prontera-collision crash was
    // traced directly to this project previously only ever loading the generic file and missing
    // this ruleset-specific overlay. This is a GENERIC fix (every map benefits from the same
    // merge order, not a Prontera-specific patch) matching the same Renewal/PreRenewal
    // distinction the rest of this project's game-data pipeline already uses (ai/world-data.md's
    // "compile-character-data" Renewal-only sourcing). The import layer exists for pinned-source
    // parity (map_readallmaps checks db/import/map_cache.dat first) even though pinned rAthena's
    // own checked-in tree ships no such file today (only db/import-tmpl/, a template directory) -
    // see LoadLayeredOverlay's own doc comment for exactly how each optional layer is resolved.
    //
    // A missing/malformed configured mapCachePath still fails startup loudly exactly as before -
    // an operator who configured map_cache_path must be told if it did not load, not left
    // believing it did. Both higher-priority layers (import, ruleset-specific) are OPTIONAL: their
    // absence is not itself an error (a deployment might genuinely not have either file available),
    // only silent if truly absent - see LoadLayeredOverlay's own doc comment for the exact
    // distinction between "file absent" (silently skipped) and "file present but malformed" (still
    // fails loudly). map_cache.dat's own map names are used verbatim as Athena's logical map
    // names: each pinned map (including int_land/int_land01../04, which the file itself declares
    // as distinct records with real geometry) is registered under its own name, with no alias
    // mechanism - see ai/world-data.md for why an alias layer is not needed for this source.
    private static IMapCollisionProvider LoadFromMapCache(string mapCachePath, RagnarokRuleSet ruleSet)
    {
        // `mapCachePath` as configured/passed in is resolved by ordinary filesystem rules, which
        // means it depends on this process's current working directory when it isn't already
        // absolute - a real production incident (Aspire's AppHost launches MapServer with a CWD
        // that is not guaranteed to be the repository root) proved that a relative
        // `map_cache_path` value silently "worked" for direct local execution and Docker (both
        // happen to have a CWD the configured relative path resolves correctly against) while
        // failing under Aspire with no clue why. Resolving and logging/reporting the ABSOLUTE path
        // explicitly - not just echoing back the original configured string - makes a future CWD
        // mismatch immediately diagnosable instead of requiring another live debugging session.
        var resolvedPath = Path.GetFullPath(mapCachePath);
        MapLogger.Status($"Map collision source: map_cache.dat configured='{mapCachePath}' resolved='{resolvedPath}'");

        if (!File.Exists(resolvedPath))
        {
            throw new InvalidOperationException(
                $"Configured map_cache_path '{mapCachePath}' (resolved to '{resolvedPath}') was not found. " +
                "If this path is relative, verify the MapServer process's working directory actually matches " +
                "where it should resolve from, or supply an absolute path via --map-cache-path.");
        }

        var dbDirectory = Path.GetDirectoryName(resolvedPath);
        var byMapName = new Dictionary<string, MapCollisionMap>(StringComparer.OrdinalIgnoreCase);

        // Highest-to-lowest priority, matching pinned map_readallmaps' own load order exactly -
        // each layer wins on collision over every layer loaded after it.
        var importPath = dbDirectory is null ? null : Path.Combine(dbDirectory, "import", "map_cache.dat");
        var importCount = importPath is null ? 0 : LoadLayeredOverlay(importPath, "import", byMapName);

        var rulesetSubdirectory = ruleSet switch
        {
            RagnarokRuleSet.Renewal => "re",
            RagnarokRuleSet.PreRenewal => "pre-re",
            _ => throw new ArgumentOutOfRangeException(nameof(ruleSet), ruleSet, "Unknown ruleset."),
        };
        var rulesetPath = dbDirectory is null ? null : Path.Combine(dbDirectory, rulesetSubdirectory, "map_cache.dat");
        var rulesetCount = rulesetPath is null ? 0 : LoadLayeredOverlay(rulesetPath, $"ruleset-specific ({ruleSet})", byMapName);

        IReadOnlyList<MapCollisionMap> baseMaps;
        try
        {
            baseMaps = RathenaMapCacheReader.ReadAllFromFile(resolvedPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new InvalidOperationException($"Configured map_cache_path '{mapCachePath}' (resolved to '{resolvedPath}') could not be read: {ex.Message}", ex);
        }

        var baseCount = 0;
        foreach (var map in baseMaps)
        {
            // A higher-priority layer already won for this name (TryAdd fails silently on a real
            // cross-layer collision - NOT the same as the duplicate-within-one-file error below,
            // which only fires for a genuine same-file duplicate).
            if (byMapName.ContainsKey(map.MapName)) continue;
            if (!byMapName.TryAdd(map.MapName, map))
            {
                throw new InvalidOperationException($"map_cache.dat contains duplicate map name '{map.MapName}'.");
            }
            baseCount++;
        }

        MapLogger.Status(
            $"Loaded map_cache.dat '{resolvedPath}': {baseCount} maps ({importCount} from import overlay, " +
            $"{rulesetCount} from ruleset-specific overlay, {byMapName.Count} total).");
        return new MapCollisionProvider(byMapName);
    }

    // One optional higher-priority overlay layer (db/import/map_cache.dat or
    // db/{re|pre-re}/map_cache.dat), resolved RELATIVE TO the configured base map_cache_path's own
    // containing "db" directory - e.g. configured ".../db/map_cache.dat" resolves the ruleset
    // overlay to ".../db/re/map_cache.dat" for Renewal, the same relative "db/" + DBPATH +
    // "map_cache.dat" layout pinned source itself uses (and "db/import/map_cache.dat" for the
    // import layer). Absence of this file is NOT an error (silently skipped, zero maps loaded from
    // it, `byMapName` untouched) - a deployment might genuinely lack it, and pinned rAthena's own
    // checked-in tree ships no db/import/map_cache.dat at all (only the db/import-tmpl/ template
    // directory) - but a PRESENT, malformed overlay file still fails startup loudly, matching
    // every other collision-source failure mode in this loader. A duplicate map name WITHIN this
    // one file still fails loudly (a genuine same-file authoring error); a name this layer shares
    // with an already-loaded higher-priority layer is normal first-match-wins layering, not an
    // error - the caller passes maps in strict priority order and relies on `TryAdd`'s no-op
    // failure for that case, never overwriting what a higher-priority layer already contributed.
    private static int LoadLayeredOverlay(string overlayPath, string layerDescription, Dictionary<string, MapCollisionMap> byMapName)
    {
        if (!File.Exists(overlayPath)) return 0;

        IReadOnlyList<MapCollisionMap> overlayMaps;
        try
        {
            overlayMaps = RathenaMapCacheReader.ReadAllFromFile(overlayPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new InvalidOperationException($"{layerDescription} map cache '{overlayPath}' could not be read: {ex.Message}", ex);
        }

        var seenInThisFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var map in overlayMaps)
        {
            if (!seenInThisFile.Add(map.MapName))
            {
                throw new InvalidOperationException($"{layerDescription} map cache '{overlayPath}' contains duplicate map name '{map.MapName}'.");
            }

            // A higher-priority layer (or an earlier call for this same layer type) already won
            // this name - normal first-match-wins layering, not an error.
            byMapName.TryAdd(map.MapName, map);
            added++;
        }

        MapLogger.Status($"Loaded {layerDescription} map cache '{overlayPath}': {added} maps.");
        return added;
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
