namespace Athena.Rathena.Data;

// Pure, deterministic first-match-wins layering over parsed RathenaMapCacheFormat.Entry sets,
// matching pinned rAthena's own map_readallmaps load order (map.cpp:3908-3943) from highest to
// lowest priority: db/import/map_cache.dat, then the ruleset-specific cache (db/re/map_cache.dat
// for Renewal), then the generic/base db/map_cache.dat. Shared by MapCollisionStartupLoader
// (production MapServer startup) and WorldDataImporter's RepositoryDomainAnalyzers (compatibility
// analysis) so both consult the exact same merged map index - see ai/world-data.md and
// MapCollisionStartupLoader's own doc comment for the full semantics this mirrors.
//
// Deliberately pure: no logging, no configuration, no MapServer/WorldDataImporter-specific types.
// Callers own presentation (logging counts, mapping into their own domain types) and error framing
// (this type reports failures as MapCacheLayerException; callers decide how to wrap/report them).
public static class RathenaMapCacheLayers
{
    public enum Provenance { Base, RenewalOverlay, ImportOverlay }

    public sealed record ResolvedMap(RathenaMapCacheFormat.Entry Entry, Provenance Source);

    // baseCache: required, already-read bytes of db/map_cache.dat.
    // renewalOverlay/importOverlay: optional (null when the layer file is absent); already-read
    // bytes of db/re/map_cache.dat and db/import/map_cache.dat respectively. Passing null for an
    // absent optional layer is the caller's responsibility (this type never touches the filesystem).
    public static IReadOnlyList<ResolvedMap> Merge(byte[] baseCache, byte[]? renewalOverlay = null, byte[]? importOverlay = null)
    {
        var byName = new Dictionary<string, ResolvedMap>(StringComparer.OrdinalIgnoreCase);

        if (importOverlay is not null) LoadLayer(importOverlay, "db/import/map_cache.dat", Provenance.ImportOverlay, byName);
        if (renewalOverlay is not null) LoadLayer(renewalOverlay, "db/re/map_cache.dat", Provenance.RenewalOverlay, byName);

        IReadOnlyList<RathenaMapCacheFormat.Entry> baseEntries;
        try
        {
            baseEntries = RathenaMapCacheFormat.ReadAll(baseCache);
        }
        catch (InvalidDataException ex)
        {
            throw new MapCacheLayerException("db/map_cache.dat", ex.Message, ex);
        }

        var seenInBase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in baseEntries)
        {
            if (!seenInBase.Add(entry.Name))
                throw new MapCacheLayerException("db/map_cache.dat", $"contains duplicate map name '{entry.Name}'.", null);
            byName.TryAdd(entry.Name, new ResolvedMap(entry, Provenance.Base));
        }

        return byName.Values.OrderBy(item => item.Entry.Name, StringComparer.Ordinal).ToArray();
    }

    private static void LoadLayer(byte[] bytes, string layerLabel, Provenance provenance, Dictionary<string, ResolvedMap> byName)
    {
        IReadOnlyList<RathenaMapCacheFormat.Entry> entries;
        try
        {
            entries = RathenaMapCacheFormat.ReadAll(bytes);
        }
        catch (InvalidDataException ex)
        {
            throw new MapCacheLayerException(layerLabel, ex.Message, ex);
        }

        var seenInLayer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!seenInLayer.Add(entry.Name))
                throw new MapCacheLayerException(layerLabel, $"contains duplicate map name '{entry.Name}'.", null);
            byName.TryAdd(entry.Name, new ResolvedMap(entry, provenance));
        }
    }
}

public sealed class MapCacheLayerException(string layer, string reason, Exception? inner)
    : Exception($"{layer}: {reason}", inner)
{
    public string Layer { get; } = layer;
}
