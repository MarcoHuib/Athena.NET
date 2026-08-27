using Athena.Net.MapServer.Logging;

namespace Athena.Net.MapServer.World;

// Picks a spawn cell for a `MobSpawnDefinition`. Called both for a monster's initial spawn AND for
// every respawn of a random-spawn declaration (MonsterRegistry/MobInstance never cache/reuse a
// previously resolved runtime coordinate as if it were the pinned declaration itself - see
// MobInstance.GetPosition) - matching pinned mob_spawn, which re-runs this same search on every
// call, initial spawn and respawn alike.
//
// Try-style (not a plain return value) because pinned rAthena distinguishes a genuine TEMPORARY
// outcome this interface must not conflate with anything else: the source-backed random search
// exhausting its attempt budget without finding a valid cell. Pinned mob_spawn itself handles
// this by giving up for THIS call and rescheduling a fresh attempt later (mob.cpp:1152-1159,
// mob_delayspawn), never by silently falling back to (0,0) or an arbitrary placeholder - see
// MobInstance.TryRespawn for how a `false` return here reuses the EXISTING respawn-retry sweep
// rather than a new scheduler. Every OTHER failure mode (a missing map, incompatible map
// dimensions, unsupported declaration geometry) is a hard error a real implementation throws for
// instead of returning `false` - see RathenaCompatibleMobSpawnCellSelector's own doc comment for
// the exact, deliberately strict breakdown of which outcome maps to which behavior.
public interface IMobSpawnCellSelector
{
    bool TrySelectCell(MobSpawnDefinition spawn, int instanceIndex, out MobPosition position);
}

// A collision-less/test/dev placeholder selector, chosen DIRECTLY and explicitly at world
// composition time (MapServerWorld.Build's own default when no collision provider is configured)
// - never reached as internal recovery from bad or incomplete collision data behind
// RathenaCompatibleMobSpawnCellSelector (see that type's own doc comment for why). It reproduces
// only the part of map_search_freecell's contract that is possible without any walkability data:
// a map-wide-looking spread of DETERMINISTIC positions (not randomized - determinism keeps
// monster placement reproducible across restarts without a persisted position store) computed
// from the spawn's own declared instance count, so distinct instances do not collide with each
// other. It does NOT check walkability, map bounds, or the real edge margin, always succeeds
// (there is no concept of "exhausted attempts" for a deterministic formula), and MUST NOT be
// described as reproducing rAthena's real spawn-cell selection.
public sealed class UnverifiedFallbackMobSpawnCellSelector : IMobSpawnCellSelector
{
    private const ushort BaseX = 50;
    private const ushort BaseY = 50;
    private const ushort Stride = 2;
    private const int RowWidth = 10;

    public bool TrySelectCell(MobSpawnDefinition spawn, int instanceIndex, out MobPosition position)
    {
        var row = instanceIndex / RowWidth;
        var column = instanceIndex % RowWidth;
        position = new MobPosition((ushort)(BaseX + column * Stride), (ushort)(BaseY + row * Stride));
        return true;
    }
}

// Source-traced reproduction of pinned rAthena's map-wide random spawn-cell
// search (mob.cpp:1117-1161 mob_spawn -> map.cpp:1798-1867
// map_search_freecell), for the ONLY case current generated declarations
// use: a pinned `<map>,0,0` line with no `xs,ys` columns (MobSpawnDefinition
// X=0,Y=0,Xs=0,Ys=0 - "xs+ys<1" per pinned mob_spawn's own guard at
// mob.cpp:1143). Explicit rectangular/fixed-center spawn areas (X/Y/Xs/Ys not
// all zero) are deliberately NOT implemented by this type yet - see
// TrySelectCell's own guard clause. Such a declaration THROWS rather than
// silently falling back to the unverified placeholder: this type represents
// "real collision-backed spawning is active for this world", so a real
// pinned declaration whose geometry it cannot yet handle is a genuine
// unsupported-feature gap that must be surfaced loudly, never masked as if
// it had been placed correctly. `UnverifiedFallbackMobSpawnCellSelector`
// remains available as a DIRECT, explicitly-chosen selector for a
// collision-less/test/dev world (MapServerWorld.Build's own default when no
// collision provider is configured) - it must never be reached silently
// from behind this type for a declaration shape this type simply hasn't
// implemented. Supporting rectangular/fixed-center areas requires
// reproducing map_search_freecell's OTHER call shape (rx=Xs-1, ry=Ys-1,
// centered on the declared X/Y), which no current generated declaration
// exercises; `MobSpawnDefinition.Xs/Ys` are still preserved losslessly by
// the converter for that future slice.
//
// Traced exactly:
//   - mob_spawn (mob.cpp:1148-1152): first attempt is `map_search_freecell`
//     with rx=ry=-1 (map-wide) and `tries=8`; if that fails AND the
//     (unusable, x=0/y=0) center cell is unreachable, a SECOND map-wide
//     attempt runs with the function's own default `tries=50`. This selector
//     reproduces that exact two-phase shape (8 tries, then up to 50 more) as
//     one combined budget rather than literally two nested loops, since the
//     center-cell recheck between phases is meaningless here (x=0,y=0 is
//     never reachable on any real map - see MapCollisionMap.IsTraversalCell's
//     own documented traversal-boundary exclusion, which already treats
//     x<0/y<0 as unreachable). Exhausting the combined budget is the
//     TEMPORARY-failure outcome (TrySelectCell returns false) matching
//     pinned mob_spawn's own "reschedule via mob_delayspawn, try again
//     later" behavior - never a thrown exception, and never a silent
//     placeholder coordinate.
//   - map_search_freecell (map.cpp:1830-1866): per attempt, `edge =
//     battle_config.map_edge_size` (default 15, MapEdgeSize below) and
//     `edge_valid = min(edge, 5)` (EdgeValid below) bound two DIFFERENT
//     things - `edge` bounds the RANDOM CANDIDATE RANGE itself
//     (`rnd_value(edge, xs-edge-1)`, both endpoints inclusive - NOT the same
//     as .NET's exclusive-upper-bound Random.Next), while `edge_valid`
//     re-validates the picked (x,y) isn't within edge_valid cells of the
//     REAL map edge (`x < edge_valid || x > xs-edge_valid`) as a defensive
//     narrower check. A candidate equal to the previous target cell is
//     skipped (`*x == bx && *y == by`) - irrelevant for the x=0/y=0 case
//     here (bx=by=0 is already excluded by the edge checks on any real map),
//     so this selector does not special-case it.
//   - map_getcellp (map.cpp:3323-3395 CELL_CHKREACH): reduces to Walkable
//     within pinned traversal bounds (x<xs-1, y<ys-1) - exactly what
//     MapCollisionMap.IsTraversalCell centralizes; see that method's own doc
//     comment for why raw MapCollisionMap.IsInBounds must NOT be used here.
//   - `flag&4` (battle_config.no_spawn_on_player, default 0/disabled) is a
//     DYNAMIC player-occupancy check this static-collision-only slice does
//     not model - out of scope per this project's current "dynamic runtime
//     cell state is a MapServer concern, never part of imported/static
//     collision data" convention (see MapCollisionMap's own doc comment).
//
// This type NEVER internally falls back to UnverifiedFallbackMobSpawnCellSelector (or fabricates
// any other coordinate) for ANY outcome - the separation is strict and deliberate:
//   supported declaration + map exists + a candidate is found         -> true (real cell)
//   supported declaration + map exists + all 58 attempts exhausted    -> false (temporary; retry)
//   requested map missing from the collision provider                 -> throws InvalidOperationException
//   map dimensions incompatible with the pinned edge margin           -> throws InvalidOperationException
//   unsupported X/Y/Xs/Ys geometry                                    -> throws NotSupportedException
// A missing map or a map too small for the pinned edge margin are WORLD-DATA/CONFIGURATION errors
// once collision-backed spawning is active for a world - not the same "no data at all, use the
// placeholder" situation UnverifiedFallbackMobSpawnCellSelector exists for. That selector is only
// ever chosen DIRECTLY at world-composition time for an explicitly collision-less/test/dev setup
// (MapServerWorld.Build's own default when no collision provider is configured); it must never be
// reached as internal recovery from bad/incomplete collision data behind this type. Confusing
// "this world intentionally has no collision data" with "this world has collision data but it's
// broken for this one map" would silently produce fake coordinates from what looks like a real,
// collision-backed world - exactly what this project's evidence/authoritative-data conventions
// forbid.
public sealed class RathenaCompatibleMobSpawnCellSelector(
    IMapCollisionProvider collisionProvider,
    Func<int, int, int> randomInclusiveRange) : IMobSpawnCellSelector
{
    // battle_config.map_edge_size (map.cpp default table, "map_edge_size", 15).
    internal const int MapEdgeSize = 15;
    // std::min(battle_config.map_edge_size, 5) - map.cpp:1831.
    internal const int EdgeValid = 5;
    // map_search_freecell's default `tries` parameter (map.hpp:1166) - reproduced here as the
    // SECOND-phase budget; the first phase's literal `tries=8` (mob.cpp:1149) is added on top,
    // for a combined 58-attempt budget (see this type's own doc comment for why the two phases
    // collapse into one combined attempt loop instead of two nested ones).
    internal const int DefaultTries = 50;
    internal const int InitialPhaseTries = 8;

    public RathenaCompatibleMobSpawnCellSelector(IMapCollisionProvider collisionProvider)
        : this(collisionProvider, DefaultRandomInclusiveRange) { }

    private static int DefaultRandomInclusiveRange(int minInclusive, int maxInclusive) =>
        System.Random.Shared.Next(minInclusive, maxInclusive + 1);

    public bool TrySelectCell(MobSpawnDefinition spawn, int instanceIndex, out MobPosition position)
    {
        // Only the proven map-wide x=0/y=0/xs=0/ys=0 case is implemented (see this type's own doc
        // comment) - any other declared spawn geometry is an unsupported-feature gap, not a
        // transient/missing-data condition, so it throws rather than silently falling back to the
        // unverified placeholder (see this type's own doc comment for why).
        if (spawn.X != 0 || spawn.Y != 0 || spawn.Xs != 0 || spawn.Ys != 0)
        {
            throw new NotSupportedException(
                $"RathenaCompatibleMobSpawnCellSelector does not yet support fixed/rectangular spawn geometry " +
                $"(X={spawn.X}, Y={spawn.Y}, Xs={spawn.Xs}, Ys={spawn.Ys}) for '{spawn.Mob.AegisName}' on map '{spawn.Map}'.");
        }

        if (!collisionProvider.TryGetMap(spawn.Map, out var map))
        {
            throw new InvalidOperationException(
                $"No collision data is loaded for map '{spawn.Map}' (spawn declaration for '{spawn.Mob.AegisName}'). " +
                "This is a world-data/configuration error, not a transient search failure - a collision-backed world " +
                "must have every map its own spawn declarations reference.");
        }

        var edgeValid = Math.Min(MapEdgeSize, EdgeValid);
        var low = MapEdgeSize;
        var high = map.Width - MapEdgeSize - 1;
        var lowY = MapEdgeSize;
        var highY = map.Height - MapEdgeSize - 1;

        // A map too small for the pinned edge margin to leave any candidate range at all can never
        // produce a valid map-wide cell under pinned semantics either (rnd_value with low > high is
        // itself undefined there) - this is a DATA problem (the map's own real dimensions can never
        // satisfy this search, no matter how many attempts run), not a transient attempt-exhaustion
        // outcome, so it throws the same way a missing map does rather than returning a retryable
        // false (retrying would never succeed - the map's dimensions never change).
        if (low > high || lowY > highY)
        {
            throw new InvalidOperationException(
                $"Map '{spawn.Map}' ({map.Width}x{map.Height}) is smaller than the pinned map-edge margin " +
                $"({MapEdgeSize}) allows for a map-wide random spawn - no candidate range exists at all.");
        }

        for (var attempt = 0; attempt < InitialPhaseTries + DefaultTries; attempt++)
        {
            var x = randomInclusiveRange(low, high);
            var y = randomInclusiveRange(lowY, highY);

            // Real-map-edge re-validation (map.cpp:1844) - a narrower, defensive re-check distinct
            // from the candidate RANGE bound above (edge, not edge_valid).
            if (x < edgeValid || x > map.Width - edgeValid || y < edgeValid || y > map.Height - edgeValid)
                continue;

            if (map.IsTraversalCell(x, y))
            {
                position = new MobPosition((ushort)x, (ushort)y);
                LogSelectedCellDiagnostics(spawn, map, position);
                return true;
            }
        }

        // Attempt budget exhausted: a genuine TEMPORARY failure (pinned mob_spawn's own
        // mob_delayspawn retry-later outcome), never a thrown exception and never a silent
        // placeholder coordinate - see this type's own doc comment.
        position = default;
        return false;
    }

    // Diagnostic-only (source-neutral: does not affect spawn eligibility - the cell was already
    // accepted via IsTraversalCell above regardless of these values). Covers BOTH initial spawn
    // and every respawn, since MonsterRegistry routes both through this same TrySelectCell call.
    // See MapClientSession.LogMonsterCellDiagnostics for the matching 0x0368
    // actor-info-click/hover diagnostic a tester can use to inspect an already-placed instance
    // live, and ai/world-data.md for why this project does not ban Water or invent a stronger
    // connectivity rule merely because a spawned cell looks visually suspicious.
    private static void LogSelectedCellDiagnostics(MobSpawnDefinition spawn, MapCollisionMap map, MobPosition position)
    {
        var flags = map.GetCell(position.X, position.Y);
        MapLogger.Info(
            $"[iRO MAP DEBUG][MONSTER CELL] mob={spawn.Mob.AegisName} map='{spawn.Map}' x={position.X} y={position.Y} " +
            $"flags='{flags}' walkable={map.IsWalkable(position.X, position.Y)} water={map.IsWater(position.X, position.Y)} " +
            $"shootable={map.IsShootable(position.X, position.Y)} traversal={map.IsTraversalCell(position.X, position.Y)}");
    }
}
