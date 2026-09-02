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

// Source-traced reproduction of pinned rAthena's spawn-cell search, covering ALL three
// declaration shapes that appear in generated data: map-wide (X=Y=Xs=Ys=0), rectangular
// (Xs/Ys > 0, symmetric or asymmetric), and fixed-point (Xs=Ys=1). Athena.NET's generated
// `MobSpawnDefinition` data is a RAW, un-normalized copy of the pinned .txt columns (confirmed:
// MobDataCompiler copies X/Y/Xs/Ys verbatim - e.g. pinned `prt_fild08,305,233,10,10` becomes
// exactly X:305,Y:233,Xs:10,Ys:10 in PrtFild08MobSpawns.cs) - pinned rAthena's OWN
// normalization (npc_parse_mob, npc.cpp:5307-5350) only ever happens at rAthena's own NPC-load
// time, which this project does not reproduce at generation time, so this selector reproduces
// that normalization here, at runtime, before running the search.
//
// Traced exactly, in three phases:
//
//   Phase 0 - normalization (npc_parse_mob, npc.cpp:5307-5350):
//     if (xs == 0 && x > 0) xs = 1;   // "Fixed X coordinate"
//     if (ys == 0 && y > 0) ys = 1;   // "Fixed Y coordinate"
//     if ((x == 0 && y == 0) || (xs == 1 && ys == 1 && !reachable(x,y)))
//         x = y = -1; xs = ys = 0;    // forced map-wide
//   Generated data never has negative Xs/Ys (MobDataCompiler's regex groups default an absent
//   column to 0, never negative), so pinned's `xs < 0`/`ys < 0` branches can never fire against
//   this project's data and are omitted rather than dead code that can never be exercised.
//
//   Phase 1 - rectangular/fixed-point search (mob_spawn, mob.cpp:1134-1161), only entered when
//   `xs + ys >= 1` after normalization (a genuinely map-wide declaration always fails this and
//   skips straight to Phase 2, unchanged from the map-wide-only behavior this type previously
//   had): the center cell gets a `1/(xs*ys)` chance to be used directly if it is itself
//   reachable (`rnd_chance(1, xs*ys)`, mob.cpp:1143 - reproduced here as
//   `randomInclusiveRange(1, xs*ys) == 1` rather than a second injectable delegate, since it is
//   exactly the same "pick uniformly from an inclusive integer range" primitive `rnd_value`
//   already provides, just checked for a specific outcome - adding a second delegate would only
//   complicate both constructors and the test file's single-sequence `SequentialRandom` helper
//   for no behavioral gain). Otherwise, up to InitialPhaseTries (8, mob.cpp:1149) candidates are
//   drawn from `[X-rx,X+rx]x[Y-ry,Y+ry]` where `rx=Xs-1,ry=Ys-1` (map.cpp:1149's own
//   rx/ry-from-Xs/Ys translation) - a fixed-point Xs=1,Ys=1 declaration collapses this to
//   rx=ry=0, i.e. every "candidate" is the exact center cell again, matching
//   map_search_freecell's own `if(!rx&&!ry)` exact-recheck branch (map.cpp:1817-1822) rather
//   than needing a separate code path. A candidate equal to the exact center is skipped inside
//   the loop (map.cpp:1838 `*x==bx && *y==by`), since the center already had its own separate
//   roll above it. If the narrow search is exhausted, Phase 2 is entered ONLY if the center
//   cell is STILL unreachable (mob.cpp:1152's own exact condition) - a reachable-but-unlucky
//   center means the declaration reports a genuine, non-escalating temporary failure instead.
//
//   Phase 2 - map-wide search (map.cpp:1798-1867, rx=ry=-1), entered directly for a genuinely
//   map-wide declaration or as Phase 1's escalation fallback: `edge = battle_config.map_edge_size`
//   (default 15, MapEdgeSize below) bounds the RANDOM CANDIDATE RANGE itself
//   (`rnd_value(edge, xs-edge-1)`, both endpoints inclusive - NOT .NET's exclusive-upper-bound
//   Random.Next), while `edge_valid = min(edge, 5)` (EdgeValid below) re-validates the picked
//   (x,y) isn't within edge_valid cells of the REAL map edge as a distinct, narrower defensive
//   check (map.cpp:1844). This phase gets its OWN fresh DefaultTries (50, map.hpp:1166's own
//   default `tries` parameter) budget - pinned mob_spawn calls map_search_freecell TWICE with
//   two INDEPENDENT budgets (8 for the narrow/rectangular attempt, 50 for the map-wide
//   fallback), never once with a combined budget. (A prior version of this type combined both
//   into one 58-attempt loop as an approximation valid only because a genuinely map-wide
//   declaration's own "center" - x=0,y=0 - is never reachable, so the two phases collapsed
//   trivially; now that Phase 1 has a real, possibly-reachable center for rectangular/fixed-point
//   declarations, the phases must stay genuinely separate, matching pinned source exactly. This
//   changes a purely map-wide declaration's own budget from 58 to 50 attempts - confirmed to
//   have no observable effect on this type's existing test suite, since no test asserts on the
//   literal attempt count.)
//   map_getcellp (map.cpp:3323-3395, CELL_CHKREACH) reduces to Walkable within pinned traversal
//   bounds (x<xs-1, y<ys-1) - exactly what MapCollisionMap.IsTraversalCell centralizes; see that
//   method's own doc comment for why raw MapCollisionMap.IsInBounds must NOT be used here.
//   `flag&4` (battle_config.no_spawn_on_player, default 0/disabled) is a DYNAMIC player-occupancy
//   check this static-collision-only slice does not model - out of scope per this project's
//   current "dynamic runtime cell state is a MapServer concern, never part of imported/static
//   collision data" convention (see MapCollisionMap's own doc comment). `battle_config.
//   randomize_center_cell` (off by default, npc.cpp:5204-5210) - an additional per-instance
//   jitter of the declared center applied once per monster before this search even begins - is
//   likewise unmodeled, by the same "only default battle_config behavior" convention.
//
// This type NEVER internally falls back to UnverifiedFallbackMobSpawnCellSelector (or fabricates
// any other coordinate) for ANY outcome - the separation is strict and deliberate:
//   any supported declaration shape + map exists + a candidate is found  -> true (real cell)
//   any supported declaration shape + map exists + all budgets exhausted -> false (temporary; retry)
//   requested map missing from the collision provider                    -> throws InvalidOperationException
//   map dimensions incompatible with the pinned edge margin              -> throws InvalidOperationException
// There is no longer an "unsupported X/Y/Xs/Ys geometry" outcome: after Phase 0's normalization,
// every declared shape that can appear in generated data (map-wide, rectangular symmetric or
// asymmetric, fixed-point) is handled by Phase 1 and/or Phase 2 - there is no residual shape left
// unimplemented, and this type no longer throws NotSupportedException for any input.
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
    // map_search_freecell's default `tries` parameter (map.hpp:1166) - the Phase 2 (map-wide)
    // budget, independent of Phase 1's own budget below (see this type's own doc comment for why
    // these are two separate budgets, not one combined loop).
    internal const int DefaultTries = 50;
    // mob_spawn's own literal `tries=8` argument for the declared-area search (mob.cpp:1149) -
    // the Phase 1 (rectangular/fixed-point) budget.
    internal const int InitialPhaseTries = 8;

    public RathenaCompatibleMobSpawnCellSelector(IMapCollisionProvider collisionProvider)
        : this(collisionProvider, DefaultRandomInclusiveRange) { }

    private static int DefaultRandomInclusiveRange(int minInclusive, int maxInclusive) =>
        System.Random.Shared.Next(minInclusive, maxInclusive + 1);

    public bool TrySelectCell(MobSpawnDefinition spawn, int instanceIndex, out MobPosition position)
    {
        if (!collisionProvider.TryGetMap(spawn.Map, out var map))
        {
            throw new InvalidOperationException(
                $"No collision data is loaded for map '{spawn.Map}' (spawn declaration for '{spawn.Mob.AegisName}'). " +
                "This is a world-data/configuration error, not a transient search failure - a collision-backed world " +
                "must have every map its own spawn declarations reference.");
        }

        // Phase 0: npc_parse_mob normalization (npc.cpp:5307-5350). Negative Xs/Ys branches are
        // omitted - generated data structurally cannot produce them (MobDataCompiler defaults an
        // absent regex group to 0, never negative).
        int x = spawn.X, y = spawn.Y, xs = spawn.Xs, ys = spawn.Ys;
        if (xs == 0 && x > 0) xs = 1;
        if (ys == 0 && y > 0) ys = 1;
        if ((x == 0 && y == 0) || (xs == 1 && ys == 1 && !map.IsTraversalCell(x, y)))
        {
            x = y = -1;
            xs = ys = 0;
        }

        // Phase 1: rectangular/fixed-point search (mob_spawn, mob.cpp:1134-1161). Skipped
        // entirely for a genuinely map-wide declaration (xs+ys<1 always fails this guard in
        // pinned source too), which falls straight through to Phase 2 below.
        if (xs + ys >= 1)
        {
            // rnd_chance(1, xs*ys) (mob.cpp:1143), reproduced via the same inclusive-range
            // primitive already used everywhere else in this type - see this type's own doc
            // comment for why no second delegate is introduced for this.
            if (randomInclusiveRange(1, xs * ys) == 1 && map.IsTraversalCell(x, y))
            {
                position = new MobPosition((ushort)x, (ushort)y);
                return true;
            }

            var rx = xs - 1;
            var ry = ys - 1;
            for (var attempt = 0; attempt < InitialPhaseTries; attempt++)
            {
                var cx = randomInclusiveRange(x - rx, x + rx);
                var cy = randomInclusiveRange(y - ry, y + ry);
                if (cx == x && cy == y) continue; // map_search_freecell skips the already-rolled center (map.cpp:1838).
                if (map.IsTraversalCell(cx, cy))
                {
                    position = new MobPosition((ushort)cx, (ushort)cy);
                    return true;
                }
            }

            // Escalate to the map-wide fallback ONLY if the center cell is also unreachable
            // (mob.cpp:1152's own exact condition) - a reachable-but-unlucky center is a
            // genuine, non-escalating temporary failure for this declaration.
            if (map.IsTraversalCell(x, y))
            {
                position = default;
                return false;
            }
        }

        // Phase 2: map-wide search (map.cpp:1798-1867, rx=ry=-1) - unchanged logic from the
        // prior map-wide-only implementation, now with its own fresh DefaultTries budget (see
        // this type's own doc comment for why this is 50, not the old combined 58).
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

        for (var attempt = 0; attempt < DefaultTries; attempt++)
        {
            var mx = randomInclusiveRange(low, high);
            var my = randomInclusiveRange(lowY, highY);

            // Real-map-edge re-validation (map.cpp:1844) - a narrower, defensive re-check distinct
            // from the candidate RANGE bound above (edge, not edge_valid).
            if (mx < edgeValid || mx > map.Width - edgeValid || my < edgeValid || my > map.Height - edgeValid)
                continue;

            if (map.IsTraversalCell(mx, my))
            {
                position = new MobPosition((ushort)mx, (ushort)my);
                return true;
            }
        }

        // Attempt budget exhausted: a genuine TEMPORARY failure (pinned mob_spawn's own
        // mob_delayspawn retry-later outcome), never a thrown exception and never a silent
        // placeholder coordinate - see this type's own doc comment.
        position = default;
        return false;
    }
}
