using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Logging;
using Athena.Rathena.Data;

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
        var rulesetSubdirectory = ruleSet switch
        {
            RagnarokRuleSet.Renewal => "re",
            RagnarokRuleSet.PreRenewal => "pre-re",
            _ => throw new ArgumentOutOfRangeException(nameof(ruleSet), ruleSet, "Unknown ruleset."),
        };
        var importPath = dbDirectory is null ? null : Path.Combine(dbDirectory, "import", "map_cache.dat");
        var rulesetPath = dbDirectory is null ? null : Path.Combine(dbDirectory, rulesetSubdirectory, "map_cache.dat");

        // Layering/merge semantics (first-match-wins, import > ruleset-specific > base) live in the
        // shared, pure RathenaMapCacheLayers so this loader and WorldDataImporter's repository-wide
        // compatibility analyzer consult exactly the same merged map index - see that type's own
        // doc comment and ai/world-data.md. This loader owns only I/O, logging, and translating the
        // parsed layers into runtime MapCollisionMap instances.
        var importBytes = importPath is not null && File.Exists(importPath) ? File.ReadAllBytes(importPath) : null;
        var rulesetBytes = rulesetPath is not null && File.Exists(rulesetPath) ? File.ReadAllBytes(rulesetPath) : null;
        var baseBytes = File.ReadAllBytes(resolvedPath);

        IReadOnlyList<RathenaMapCacheLayers.ResolvedMap> merged;
        try
        {
            merged = RathenaMapCacheLayers.Merge(baseBytes, rulesetBytes, importBytes);
        }
        catch (MapCacheLayerException ex)
        {
            throw new InvalidOperationException($"Map cache layer '{ex.Layer}' (resolved relative to '{resolvedPath}') could not be read: {ex.Message}", ex);
        }

        var byMapName = new Dictionary<string, MapCollisionMap>(StringComparer.OrdinalIgnoreCase);
        var baseCount = 0; var rulesetCount = 0; var importCount = 0;
        foreach (var resolved in merged)
        {
            byMapName.Add(resolved.Entry.Name, ToMapCollisionMap(resolved.Entry));
            switch (resolved.Source)
            {
                case RathenaMapCacheLayers.Provenance.ImportOverlay: importCount++; break;
                case RathenaMapCacheLayers.Provenance.RenewalOverlay: rulesetCount++; break;
                default: baseCount++; break;
            }
        }

        MapLogger.Status(
            $"Loaded map_cache.dat '{resolvedPath}': {baseCount} maps ({importCount} from import overlay, " +
            $"{rulesetCount} from ruleset-specific overlay, {byMapName.Count} total).");
        return new MapCollisionProvider(byMapName);
    }

    // Mirrors RathenaMapCacheReader.ReadAll's own private cell-mapping (map_gat2cell,
    // map.cpp:3280-3299), duplicated here rather than exposed there because this loader decodes
    // through RathenaMapCacheLayers.Merge directly (to recover per-map provenance for logging),
    // bypassing RathenaMapCacheReader's single-file, no-provenance convenience path.
    private static MapCollisionMap ToMapCollisionMap(RathenaMapCacheFormat.Entry entry) =>
        new(entry.Name, entry.Width, entry.Height, entry.RawCells.Select((cell, index) => GatTypeToFlags(cell, entry.Name, index)).ToArray());

    private static MapCellFlags GatTypeToFlags(byte gatType, string mapName, int cellIndex) => gatType switch
    {
        0 or 2 or 4 or 6 => MapCellFlags.Walkable | MapCellFlags.Shootable,
        1 => MapCellFlags.None,
        3 => MapCellFlags.Walkable | MapCellFlags.Shootable | MapCellFlags.Water,
        5 => MapCellFlags.Shootable,
        _ => throw new InvalidDataException($"map_cache.dat map '{mapName}' cell {cellIndex} has unrecognized GAT type {gatType}."),
    };

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
