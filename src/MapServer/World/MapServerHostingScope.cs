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
// Current scope covers exactly what Athena.NET genuinely hosts today: the tutorial family (iz_int,
// int_land), Izlude, the Izlude -> prt_fild08 -> Prontera corridor - ONE canonical copy of each.
// Pinned rAthena also ships parallel "channel" copies of these maps (iz_int01..04, int_land01..04,
// izlude_a..d, prt_fild08a..d, iz_ac01_a..d, iz_ac02_a..d - the 24-entry alias table in
// Shared/MapIdentity/CanonicalMapPolicy.cs). Athena.NET does not host channels: those maps are not
// served, own no World simulation, and every character location / warp destination naming one is
// canonicalized onto its canonical map at the CharServer read/write boundary and at MapServer's
// TeleportTo. Their pinned/generated source stays in the repository purely as source coverage
// (source representation is not runtime activation). This remains an explicit scope decision, not a
// derivation from collision coverage. MapCollisionStartupLoader's ruleset-specific overlay merge
// resolves the canonical maps' collision (`legacy/rathena/db/re/map_cache.dat` contains a real
// `prt_fild08` record - see `MapCollisionStartupLoaderTests.Load_RenewalRuleSet_RealPinnedMapCache_
// PrtFild08BaseMapNowResolvesViaOverlay`).
public static class MapServerHostingScope
{
    public static readonly IReadOnlySet<string> ServedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "int_land",
        "iz_int",
        "izlude",
        "prt_fild08",
        "prontera",
    };

    // Gameplay map hosting and generated monster activation are intentionally separate scopes -
    // this set remains a deliberate, hand-declared scope decision, never derived from ServedMaps
    // or from collision-data availability. It previously excluded prt_fild08/a/b/c because
    // RathenaCompatibleMobSpawnCellSelector only implemented pinned rAthena's map-wide spawn
    // search and threw NotSupportedException for the base map's real rectangular declarations
    // (e.g. the Poring spawn at legacy/rathena/npc/re/mobs/fields/prontera.txt:97,
    // X:305,Y:233,Xs:10,Ys:10) - that gap is now closed (see MobSpawnCellSelector.cs's own doc
    // comment for the full rectangular/fixed-point search this selector now reproduces), so the
    // canonical prt_fild08 map is included below (its channel copies are not - see the type comment).
    public static readonly IReadOnlySet<string> MobSpawnMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "int_land",
        "prt_fild08",
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
