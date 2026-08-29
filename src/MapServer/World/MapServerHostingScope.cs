namespace Athena.Net.MapServer.World;

// Explicit runtime/deployment hosting scope: which maps THIS Athena.NET build actually serves.
// Deliberately hand-declared, not derived from the warp graph (WorldMapRegistry.ReachableMaps is a
// diagnostic-only view - see its own doc comment), not derived from collision-data availability,
// and not inferred from any other generated-content signal. "Served" and "reachable via a warp"
// are different concepts: a map can be served with no static warp leading to it at all (a
// character start_point, a persisted reconnect position, a save point, or a future non-warp entry
// mechanism), and a map with generated content is not automatically served merely because content
// exists for it - see MapServerWorld.Build's `servedMaps` parameter for the runtime consequence
// (a served map's generated monster spawns are instantiated normally, and fail loudly if collision
// data is then missing; an unserved map's generated spawns are retained as source truth but never
// instantiated).
//
// Current scope covers exactly what Athena.NET genuinely hosts today: the tutorial `int_land`
// family (base + 01-04 instanced duplicates) and the Izlude -> prt_fild08d -> Prontera travel
// corridor (ai/world-data.md's "Travel corridor" section). Plain `prt_fild08` is deliberately
// EXCLUDED: pinned `legacy/rathena/db/map_cache.dat` has no collision data for that specific
// generic/base family member (only its `a`/`b`/`c`/`d` instanced duplicates), so this build does
// not yet serve it - its generated mob definitions/spawns remain complete and source-backed (see
// PrtFild08MobSpawns.cs), they are simply not instantiated until real collision data exists.
public static class MapServerHostingScope
{
    public static readonly IReadOnlySet<string> ServedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "int_land", "int_land01", "int_land02", "int_land03", "int_land04",
        "iz_int", "iz_int01", "iz_int02", "iz_int03", "iz_int04",
        "izlude_d",
        "prt_fild08d",
        "prontera",
    };
}
