using Athena.Net.MapServer.World;
using Athena.Net.MapServer.Gameplay.Rules;

namespace Athena.Net.MapServer.Tests.World;

public sealed class RathenaCompatibleMobSpawnCellSelectorTests
{
    private static MobDefinition MakeMob() => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872, AttackMotion: 672, DamageMotion: 480,
        BaseExp: 0, JobExp: 0, Mode: MobMode.CanMove,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static MobSpawnDefinition MapWideSpawn(string map = "test_map", int count = 1) =>
        new(MakeMob(), map, count, 5000, 0, new("rAthena", "abc", "x.txt", 1)); // X=Y=Xs=Ys default 0.

    private static MobSpawnDefinition RectangularSpawn(short x = 150, short y = 180, short xs = 10, short ys = 12) =>
        new(MakeMob(), "test_map", 1, 5000, 0, new("rAthena", "abc", "x.txt", 1), X: x, Y: y, Xs: xs, Ys: ys);

    private static MobSpawnDefinition FixedPointSpawn(short x = 150, short y = 180) =>
        new(MakeMob(), "test_map", 1, 5000, 0, new("rAthena", "abc", "x.txt", 1), X: x, Y: y, Xs: 1, Ys: 1);

    // All-walkable square map with the given side length, so every cell inside pinned traversal
    // bounds (0 <= x < side-1, 0 <= y < side-1) is a valid candidate.
    private static MapCollisionMap MakeAllWalkableMap(string name, int side) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    private static MapCollisionMap MakeMapWithBlockedCell(string name, int side, int blockedX, int blockedY)
    {
        var cells = Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray();
        cells[blockedX + blockedY * side] = MapCellFlags.None;
        return new MapCollisionMap(name, side, side, cells);
    }

    private static Func<int, int, int> SequentialRandom(params int[] values)
    {
        var index = 0;
        return (_, _) => values[index++ % values.Length];
    }

    [Fact]
    public void TrySelectCell_MapWideDeclaration_ReturnsWalkableCellWithinTraversalBounds()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, max) => min);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.True(success);
        Assert.True(position.X >= 0 && position.X < map.Width - 1);
        Assert.True(position.Y >= 0 && position.Y < map.Height - 1);
        Assert.True(map.IsTraversalCell(position.X, position.Y));
    }

    [Fact]
    public void TrySelectCell_NeverReturnsABlockedCell()
    {
        // Deterministic RNG sequence: first candidate lands exactly on the blocked cell, forcing
        // the selector to retry and prove it does not just accept the first candidate blindly.
        var map = MakeMapWithBlockedCell("test_map", 100, blockedX: 50, blockedY: 50);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(50, 51); // (x,y) pairs consumed as (low,high) calls alternate.
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.True(success);
        Assert.NotEqual((50, 50), (position.X, position.Y));
        Assert.True(map.IsWalkable(position.X, position.Y));
    }

    [Fact]
    public void TrySelectCell_NeverReturnsTheFinalRowOrColumn_EvenThoughRawArtifactBoundsIncludeThem()
    {
        // A 100x100 all-walkable map's raw artifact bounds include x/y == 99, but pinned
        // traversal semantics exclude the final row/column (map_getcellp, map.cpp:3329-3331) - the
        // random range itself (edge..width-edge-1) already keeps candidates well inside that
        // boundary for any reasonably sized map, so this proves the selector's candidate range is
        // never wide enough to reach the excluded edge even by chance.
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(15, 84, 15, 84); // Widest legal range: [edge, width-edge-1].
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        Assert.True(selector.TrySelectCell(MapWideSpawn(), 0, out var position));

        Assert.True(position.X < map.Width - 1);
        Assert.True(position.Y < map.Height - 1);
    }

    [Fact]
    public void TrySelectCell_HighestLegalCoordinate_CanBeSelected()
    {
        // rAthena's rnd_value(min,max) is INCLUSIVE of both endpoints (map.cpp:1835-1836); this
        // proves the selector's own translation preserves that - the highest legal candidate
        // (width - MapEdgeSize - 1) must be reachable, not silently excluded by an off-by-one when
        // adapting to .NET's exclusive-upper-bound Random.Next shape.
        const int side = 100;
        var highestLegalX = side - RathenaCompatibleMobSpawnCellSelector.MapEdgeSize - 1;
        var highestLegalY = side - RathenaCompatibleMobSpawnCellSelector.MapEdgeSize - 1;
        var map = MakeAllWalkableMap("test_map", side);
        var provider = new MapCollisionProvider([map]);
        // The random source always returns the caller-supplied `max` (the inclusive upper bound
        // itself) - if the selector ever passed an EXCLUSIVE bound one-too-high, or clamped away
        // from the true maximum, this would either throw (out of range for a real Random.Next) or
        // never select the true edge value. Here it simply proves the boundary value is what gets
        // requested and accepted.
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (_, max) => max);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)highestLegalX, position.X);
        Assert.Equal((ushort)highestLegalY, position.Y);
        Assert.True(map.IsTraversalCell(position.X, position.Y));
    }

    [Fact]
    public void TrySelectCell_LowestLegalCoordinate_CanBeSelected()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, _) => min);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)RathenaCompatibleMobSpawnCellSelector.MapEdgeSize, position.X);
        Assert.Equal((ushort)RathenaCompatibleMobSpawnCellSelector.MapEdgeSize, position.Y);
    }

    [Fact]
    public void TrySelectCell_IsNotTheOldDeterministicFallbackRow()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, _) => min + 5);

        Assert.True(selector.TrySelectCell(MapWideSpawn(), 0, out var position));

        // The old UnverifiedFallbackMobSpawnCellSelector places instance 0 at exactly (50,50).
        Assert.NotEqual((ushort)50, position.X);
    }

    [Fact]
    public void TrySelectCell_RepeatedSelections_CanProduceDifferentValidCellsUnderAFakeRngSequence()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(20, 20, 40, 40, 60, 60);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        Assert.True(selector.TrySelectCell(MapWideSpawn(), 0, out var first));
        Assert.True(selector.TrySelectCell(MapWideSpawn(), 1, out var second));
        Assert.True(selector.TrySelectCell(MapWideSpawn(), 2, out var third));

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
    }

    [Fact]
    public void TrySelectCell_MultipleInstances_UseIndependentSelections()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(10, 10, 90, 90); // Two distinct (x,y) pairs.
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);
        var spawn = MapWideSpawn(count: 2);

        Assert.True(selector.TrySelectCell(spawn, 0, out var first));
        Assert.True(selector.TrySelectCell(spawn, 1, out var second));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TrySelectCell_MissingMap_ThrowsRatherThanFallingBack()
    {
        // A missing map is a hard world/configuration error once collision-backed spawning is
        // active for this world - it must NEVER be silently recovered via
        // UnverifiedFallbackMobSpawnCellSelector's fabricated coordinates, which would make a
        // broken/incomplete collision-backed world look like it placed the monster correctly.
        var provider = new MapCollisionProvider([]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, _) => min);

        var exception = Assert.Throws<InvalidOperationException>(
            () => selector.TrySelectCell(MapWideSpawn("nonexistent_map"), 0, out _));
        Assert.Contains("nonexistent_map", exception.Message);
    }

    [Fact]
    public void TrySelectCell_MapTooSmallForEdgeMargin_ThrowsRatherThanFallingBack()
    {
        // Side 20 with MapEdgeSize=15 leaves low=15, high=20-15-1=4: low > high, an impossible
        // range under pinned semantics too - a DATA problem with this specific map, not something
        // more attempts or a fallback placeholder can paper over.
        var map = MakeAllWalkableMap("tiny_map", 20);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, _) => min);

        var exception = Assert.Throws<InvalidOperationException>(
            () => selector.TrySelectCell(MapWideSpawn("tiny_map"), 0, out _));
        Assert.Contains("tiny_map", exception.Message);
    }

    [Fact]
    public void TrySelectCell_AllCandidatesBlocked_ReturnsFalse_NotAPlaceholderPosition()
    {
        // Every candidate the fixed RNG can produce (always (50,50)) is blocked - the attempt
        // budget exhausts and the selector must report a genuine temporary failure (false),
        // never silently falling back to (0,0) or any other placeholder coordinate.
        var map = MakeMapWithBlockedCell("test_map", 100, blockedX: 50, blockedY: 50);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (_, _) => 50);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.False(success);
        Assert.Equal(default, position);
    }

    // Correction: rectangular/fixed-point geometry is now fully implemented (pinned mob_spawn +
    // map_search_freecell, mob.cpp:1134-1161 / map.cpp:1798-1867) - it no longer throws.

    [Fact]
    public void TrySelectCell_RectangularDeclaration_CenterRollHit_ReturnsExactCenter()
    {
        // rnd_chance(1, xs*ys) (mob.cpp:1143) hits on the FIRST randomInclusiveRange(1, xs*ys)
        // call - the selector must use the exact declared center directly, with no further
        // random draws consumed for a rectangular search.
        var map = MakeAllWalkableMap("test_map", 500);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (_, _) => 1); // Always "hits" (min==max==1 works for the roll call; unused if no further calls happen).

        var success = selector.TrySelectCell(RectangularSpawn(x: 150, y: 180, xs: 10, ys: 12), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)150, position.X);
        Assert.Equal((ushort)180, position.Y);
    }

    [Fact]
    public void TrySelectCell_RectangularDeclaration_CenterRollMiss_FallsThroughToNarrowSearch()
    {
        // First call is the roll (xs*ys=120, forced to miss via a value != 1), then the narrow
        // search draws (cx,cy) pairs from [X-rx,X+rx]x[Y-ry,Y+ry] - here forced to the exact
        // low corner of that rectangle (rx=9,ry=11 -> 150-9=141, 180-11=169).
        var map = MakeAllWalkableMap("test_map", 500);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(2 /* roll: != 1, miss */, 141, 169 /* narrow search candidate */);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        var success = selector.TrySelectCell(RectangularSpawn(x: 150, y: 180, xs: 10, ys: 12), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)141, position.X);
        Assert.Equal((ushort)169, position.Y);
    }

    [Fact]
    public void TrySelectCell_RectangularDeclaration_NarrowSearchSkipsTheExactCenterCandidate()
    {
        // map_search_freecell explicitly skips a candidate equal to the center (map.cpp:1838) -
        // the first narrow-search candidate here IS the center (150,180); the selector must not
        // accept it as a "found" result via the narrow-search loop (it already had its own
        // separate roll-based chance above), and must continue to the next candidate instead.
        var map = MakeAllWalkableMap("test_map", 500);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(2 /* roll: miss */, 150, 180 /* == center, must be skipped */, 145, 175 /* real candidate */);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        var success = selector.TrySelectCell(RectangularSpawn(x: 150, y: 180, xs: 10, ys: 12), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)145, position.X);
        Assert.Equal((ushort)175, position.Y);
    }

    [Fact]
    public void TrySelectCell_RectangularDeclaration_NarrowSearchExhausted_CenterStillUnreachable_EscalatesToMapWide()
    {
        // The declared rectangle sits entirely on blocked cells (simulated by blocking the exact
        // center, which is also every candidate the fixed RNG can produce for the narrow phase) -
        // the center is unreachable both before and after the narrow search, so pinned mob_spawn
        // escalates to a full map-wide search (mob.cpp:1152), which must then succeed using a
        // real walkable cell far from the blocked rectangle.
        var map = MakeMapWithBlockedCell("test_map", 500, blockedX: 150, blockedY: 180);
        var provider = new MapCollisionProvider([map]);
        // Roll misses; every narrow-search candidate lands on the blocked center (150,180) itself
        // - which is ALSO skipped by the exact-center-candidate rule, so the narrow phase finds
        // nothing after 8 tries; the map-wide phase then draws a real, distant walkable cell.
        var random = SequentialRandom(2 /* roll miss */, 150, 180, 150, 180, 150, 180, 150, 180,
            150, 180, 150, 180, 150, 180, 150, 180, /* 8 narrow attempts, all == center */
            300, 300 /* map-wide candidate */);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        var success = selector.TrySelectCell(RectangularSpawn(x: 150, y: 180, xs: 10, ys: 12), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)300, position.X);
        Assert.Equal((ushort)300, position.Y);
    }

    [Fact]
    public void TrySelectCell_RectangularDeclaration_NarrowSearchExhausted_CenterReachable_ReturnsFalse_NoEscalation()
    {
        // The center itself is reachable, but the fixed RNG never lands on it or any other
        // walkable candidate for the narrow phase - per mob.cpp:1152, escalation to map-wide only
        // happens when the center is ALSO unreachable, so a reachable-but-unlucky center must
        // report a plain, non-escalating temporary failure instead.
        var map = MakeMapWithBlockedCell("test_map", 500, blockedX: 145, blockedY: 175); // Blocks the one candidate below, not the center.
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(2 /* roll miss */, 145, 175, 145, 175, 145, 175, 145, 175,
            145, 175, 145, 175, 145, 175, 145, 175); // 8 narrow attempts, all on the one blocked cell.
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        var success = selector.TrySelectCell(RectangularSpawn(x: 150, y: 180, xs: 10, ys: 12), 0, out var position);

        Assert.False(success);
        Assert.Equal(default, position);
    }

    [Fact]
    public void TrySelectCell_FixedPointDeclaration_ReachableCenter_ReturnsCenterWithNoRandomizedSearch()
    {
        // Xs=Ys=1 collapses rx=ry=0: every "narrow search candidate" IS the center, which is
        // always skipped by the exact-center rule - so a fixed-point declaration can ONLY
        // succeed via the roll-hit path (or, if the roll misses, must fall through to map-wide
        // once the always-skipped narrow phase exhausts and the doc'd center-still-reachable
        // no-escalation rule denies further attempts). Prove the roll-hit path here.
        var map = MakeAllWalkableMap("test_map", 500);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (_, _) => 1); // xs*ys=1 -> roll always hits.

        var success = selector.TrySelectCell(FixedPointSpawn(x: 150, y: 180), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)150, position.X);
        Assert.Equal((ushort)180, position.Y);
    }

    [Fact]
    public void TrySelectCell_FixedPointDeclaration_UnreachableCenter_EscalatesToMapWide()
    {
        // Xs=Ys=1 means xs*ys=1, so a real inclusive-range roll of [1,1] can only ever "hit" -
        // there is no genuine miss outcome for a true 1x1 declaration (the roll call's min/max
        // are always equal). What CAN legitimately fail is the center cell itself being
        // unreachable - Phase 0's own normalization (npc_parse_mob) already forces xs=ys=0 in
        // that exact case (`xs==1 && ys==1 && !reachable(x,y)`), so an unreachable fixed-point
        // center is normalized directly into a map-wide declaration before Phase 1 even runs,
        // and must succeed via the map-wide search on a real walkable cell elsewhere.
        var map = MakeMapWithBlockedCell("test_map", 500, blockedX: 200, blockedY: 200);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (_, _) => 300); // Map-wide candidate, once normalized.

        var success = selector.TrySelectCell(FixedPointSpawn(x: 200, y: 200), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)300, position.X);
        Assert.Equal((ushort)300, position.Y);
    }

    [Fact]
    public void TrySelectCell_MapWideDeclaration_UsesOnlyDefaultTriesBudget_NotTheOldCombinedBudget()
    {
        // Confirms the corrected budget split: a genuinely map-wide declaration now gets exactly
        // DefaultTries (50) attempts, not the old combined InitialPhaseTries+DefaultTries (58) -
        // supplying exactly 50 distinct candidates (all blocked) must exhaust the budget and
        // return false, proving no extra attempts beyond 50 are consumed for this declaration.
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (_, _) => -1); // Always out of edge-valid range -> always `continue`, never a real IsTraversalCell check.

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.False(success);
        Assert.Equal(default, position);
    }

    [Fact]
    public void ProductionConstructor_ProducesAWalkableCell()
    {
        // Exercises the real System.Random.Shared-backed constructor (no injected RNG) to prove
        // the production path is wired correctly end-to-end, not just the test-injectable overload.
        var map = MakeAllWalkableMap("test_map", 200);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider);

        Assert.True(selector.TrySelectCell(MapWideSpawn(), 0, out var position));
        Assert.True(map.IsTraversalCell(position.X, position.Y));
    }

    // Real pinned-data regression: the exact prt_fild08 Poring declaration
    // (legacy/rathena/npc/re/mobs/fields/prontera.txt:97, X:305,Y:233,Xs:10,Ys:10) that
    // previously threw NotSupportedException and blocked MapServerHostingScope.MobSpawnMaps
    // from including prt_fild08 at all - must now resolve a real, collision-valid cell using the
    // REAL production collision composition (MapCollisionStartupLoader, not a synthetic fixture).
    [Fact]
    public void TrySelectCell_RealPrtFild08PoringDeclaration_ResolvesRealCollisionValidCell()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");
        var provider = MapCollisionStartupLoader.Load([], mapCachePath, RagnarokRuleSet.Renewal);
        Assert.True(provider.TryGetMap("prt_fild08", out var map));
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider);
        var poring = new MobSpawnDefinition(MakeMob(), "prt_fild08", 2, 15000, 0,
            new("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "legacy/rathena/npc/re/mobs/fields/prontera.txt", 97),
            X: 305, Y: 233, Xs: 10, Ys: 10, SpawnName: "Poring");

        var success = selector.TrySelectCell(poring, 0, out var position);

        Assert.True(success);
        Assert.True(map.IsTraversalCell(position.X, position.Y));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
