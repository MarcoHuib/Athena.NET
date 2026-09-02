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
// Current scope covers exactly what Athena.NET genuinely hosts today: the complete tutorial family
// and its five source-corresponding Izlude -> prt_fild08 -> Prontera corridors. CharServer's pinned
// start-point configuration selects iz_int or iz_int01..04; the generated scripts consequently
// derive izlude, izlude_a..d and the matching prt_fild08, prt_fild08a..d. Hosting only the `d`
// member stranded legitimate `01` players on izlude_a. This remains an explicit scope decision,
// not a derivation from collision coverage. An earlier version of this comment claimed pinned
// `legacy/rathena/db/map_cache.dat` had no collision data for `prt_fild08` at all - that claim is
// now KNOWN STALE. MapCollisionStartupLoader's ruleset-specific overlay merge (added to fix the
// live Prontera collision crash - see ai/map-server.md's "Live stock-iRO acceptance fixes"
// section) resolved this incidentally: pinned `legacy/rathena/db/re/map_cache.dat` genuinely
// contains a real `prt_fild08` record (400x400) alongside its `a`/`b`/`c`/`d` instanced
// duplicates. See `MapCollisionStartupLoaderTests.Load_RenewalRuleSet_RealPinnedMapCache_
// PrtFild08BaseMapNowResolvesViaOverlay` for the regression proof this data now exists.
public static class MapServerHostingScope
{
    public static readonly IReadOnlySet<string> ServedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "int_land", "int_land01", "int_land02", "int_land03", "int_land04",
        "iz_int", "iz_int01", "iz_int02", "iz_int03", "iz_int04",
        "izlude", "izlude_a", "izlude_b", "izlude_c", "izlude_d",
        "prt_fild08", "prt_fild08a", "prt_fild08b", "prt_fild08c", "prt_fild08d",
        "prontera",
    };

    // Gameplay map hosting and generated monster activation are intentionally separate scopes.
    // Phase 2A adds the complete source-backed travel corridor, but does not expand monster-runtime
    // coverage into the newly hosted prt_fild08 family members (some use rectangular spawn geometry
    // that is outside this phase). Preserve the previously proven populations only.
    public static readonly IReadOnlySet<string> MobSpawnMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "int_land", "int_land01", "int_land02", "int_land03", "int_land04",
        "prt_fild08d",
        "prontera",
    };

    // Live stock-iRO acceptance found a real gap this project's PREVIOUS collision guard
    // (MapServerWorld.RequireRealCollisionSourceIfMobSpawnsExist) could not catch: a served map
    // with ZERO generated monster spawns (e.g. "prontera" - no mob spawn declarations target it at
    // all) still needs real collision data for ordinary PLAYER MOVEMENT, but that prior guard only
    // ever checks collision existence indirectly through GeneratedScriptRegistry.MobSpawns. A
    // player who reconnects (or transitions in) with no monster anywhere nearby could therefore
    // reach RathenaCompatibleMovementPathProvider/RathenaCompatibleMobSpawnCellSelector with no
    // collision data loaded for their own map at all, surfacing as a live
    // "No collision data is loaded for map 'X'" crash on the FIRST movement request - exactly the
    // Prontera crash reproduced on head 57dc569 (auth succeeds, bootstrap succeeds, first 0x035F
    // throws).
    //
    // This is therefore a DIFFERENT, broader invariant than the mob-spawn guard: every map this
    // build DECLARES it serves (MapServerHostingScope.ServedMaps, an explicit hand-declared set -
    // see this type's own doc comment for why it is never derived from collision coverage) must
    // have real collision data BEFORE MapServer starts listening for clients, regardless of
    // whether any monster happens to spawn there. Deliberately NOT placed inside MonsterRegistry
    // (which has no concept of "declared hosting scope" at all, only "which spawns was I actually
    // given") - this is a pure hosting-scope/composition-root concern, checked once at startup by
    // the SAME caller (MapServerApp.RunAsync) that already calls
    // RequireRealCollisionSourceIfMobSpawnsExist, before MapServerWorld.Build ever runs.
    //
    // Semantics (never derives ServedMaps from collision, never derives collision requirements
    // from ServedMaps beyond this exact check):
    //   unserved map, collision absent  -> allowed (this method says nothing about it)
    //   served map,   collision present -> allowed
    //   served map,   collision absent  -> throws, naming EVERY missing served map (not just the
    //                                       first found), so a single fix/rerun surfaces the
    //                                       complete gap rather than one map at a time.
    public static void RequireCollisionForAllServedMaps(IMapCollisionProvider collisionProvider)
    {
        var missing = ServedMaps.Where(map => !collisionProvider.TryGetMap(map, out _)).OrderBy(map => map, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"MapServerHostingScope.ServedMaps declares {missing.Length} map(s) with no collision data loaded: " +
                $"{string.Join(", ", missing)}. Every declared-served map must have real collision data before " +
                "MapServer starts listening for clients - regenerate/repair the Athena Map Pack (or configure an " +
                "explicit map_cache_path/map_collision_artifact override) so these maps resolve, or remove them " +
                "from ServedMaps if this build genuinely does not serve them yet.");
        }
    }
}
