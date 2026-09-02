namespace Athena.Net.MapServer.Tests.World;

using Athena.Net.MapServer.World;
using Athena.Net.MapServer.Gameplay.Rules;

// Source-traced A* reproduction (path.cpp:269-432 path_search's non-easy branch) - see
// RathenaCompatibleMovementPathProvider's own doc comment for the exact pinned-source mapping.
// This is the ONE pathfinding foundation player movement validation, monster idle movement, and
// future chase/attack-approach all share - tests here therefore validate generic path-computation
// correctness, not anything Poring-specific.
public sealed class RathenaCompatibleMovementPathProviderTests
{
    private static MapCollisionMap MakeAllWalkableMap(string name, int side) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    private static MapCollisionMap MakeMapFromAscii(string name, string[] rows)
    {
        var height = rows.Length;
        var width = rows[0].Length;
        var cells = new MapCellFlags[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = rows[height - 1 - y]; // Row 0 of the ASCII art is the TOP (highest y).
            for (var x = 0; x < width; x++)
                cells[x + y * width] = row[x] == '#' ? MapCellFlags.None : MapCellFlags.Walkable;
        }
        return new MapCollisionMap(name, width, height, cells);
    }

    [Fact]
    public void ComputePath_StraightUnobstructedPath_ReturnsDirectRoute()
    {
        var map = MakeAllWalkableMap("test_map", 20);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 5, 5, 5, 10);

        Assert.Equal((ushort)5, path[0].X);
        Assert.Equal((ushort)5, path[0].Y);
        Assert.Equal((ushort)5, path[^1].X);
        Assert.Equal((ushort)10, path[^1].Y);
        Assert.All(path, cell => Assert.True(map.IsTraversalCell(cell.X, cell.Y)));
    }

    [Fact]
    public void ComputePath_SameStartAndDestination_ReturnsSingleCell()
    {
        var map = MakeAllWalkableMap("test_map", 20);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 5, 5, 5, 5);

        Assert.Single(path);
        Assert.Equal((ushort)5, path[0].X);
        Assert.Equal((ushort)5, path[0].Y);
    }

    [Fact]
    public void ComputePath_RouteAroundObstruction_AvoidsBlockedCells()
    {
        // A vertical wall with a gap - the path must route through the gap, never through '#'.
        var map = MakeMapFromAscii("test_map",
        [
            ".........",
            ".........",
            ".........",
            ".........",
            "....#....", // Gap at column 4 in this row only.
            "....#....",
            "###.#####",
            ".........",
            ".........",
        ]);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 1, 1, 7, 7);

        Assert.NotEmpty(path);
        Assert.Equal((ushort)1, path[0].X);
        Assert.Equal((ushort)1, path[0].Y);
        Assert.Equal((ushort)7, path[^1].X);
        Assert.Equal((ushort)7, path[^1].Y);
        Assert.All(path, cell => Assert.True(map.IsWalkable(cell.X, cell.Y), $"({cell.X},{cell.Y}) is blocked"));
    }

    [Fact]
    public void ComputePath_UnreachableDestination_ReturnsEmpty()
    {
        // Destination fully enclosed by walls - genuinely unreachable.
        var map = MakeMapFromAscii("test_map",
        [
            ".........",
            ".........",
            "...###...",
            "...#.#...",
            "...###...",
            ".........",
            ".........",
        ]);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 1, 1, 4, 3);

        Assert.Empty(path);
    }

    [Fact]
    public void ComputePath_DestinationOnBlockedCell_ReturnsEmpty()
    {
        var map = MakeMapFromAscii("test_map",
        [
            "....",
            "..#.",
            "....",
        ]);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 0, 0, 2, 1);

        Assert.Empty(path);
    }

    // Pinned add_path (path.cpp:219-244) explicitly REOPENS a node that was already closed once a
    // strictly better g_cost is found for it ("Put it in open set again", path.cpp:229-231) -
    // required precisely because path_search's own heuristic is deliberately inadmissible
    // (Manhattan*MOVE_COST, path.cpp:55: "inadmissible (overestimating) heuristic used by game
    // client"), so closing a node in heuristic-priority order does not guarantee its g_cost can
    // never improve later, unlike under an admissible heuristic. This obstacle layout was found by
    // brute-force search specifically because it makes A* close (5,9)/(6,9)/(7,9) with a
    // suboptimal g_cost before later discovering a cheaper route through them - refusing to reopen
    // (i.e. treating `closed` as a permanent barrier once set, the bug this test guards against)
    // produces a real, different, and strictly MORE EXPENSIVE path on this exact map.
    [Fact]
    public void ComputePath_ReopensAClosedNodeWhenACheaperRouteIsLaterFound()
    {
        var map = MakeMapFromAscii("test_map",
        [
            "............",
            ".........#..",
            ".......#....",
            "........#...",
            "...#.....##.",
            "....#.......",
            ".....#......",
            "............",
            "............",
            "............",
            "..#.........",
            "............",
        ]);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 1, 1, 10, 10);

        Assert.NotEmpty(path);
        Assert.All(path, cell => Assert.True(map.IsTraversalCell(cell.X, cell.Y)));

        var cost = 0;
        for (var i = 1; i < path.Count; i++)
        {
            var dx = Math.Abs(path[i].X - path[i - 1].X);
            var dy = Math.Abs(path[i].Y - path[i - 1].Y);
            cost += dx == 1 && dy == 1 ? 14 : 10;
        }

        // Independently computed (Python A* reference reproducing this exact add_path reopening
        // rule) optimal cost for this map/start/destination is 182. Refusing to reopen closed
        // nodes finds a real but suboptimal 188-cost path instead - this assertion fails under
        // that regression, proving reopening is actually exercised and actually matters here, not
        // merely that "a path exists".
        Assert.Equal(182, cost);
    }

    [Fact]
    public void ComputePath_IllegalDiagonalCornerCut_IsRejected()
    {
        // Two walls meeting at a corner: a naive 8-directional search could "cut the corner"
        // diagonally from (0,1) to (1,2) even though both (1,1) and (0,2) are blocked. Pinned
        // path_search explicitly forbids this (path.cpp:358-361) - the path must detour around,
        // never cut through the corner. Map is deliberately larger than the corner itself so the
        // destination is not on the raw artifact's final row/column, which pinned traversal
        // bounds (IsTraversalCell) always exclude regardless of the corner-cutting rule.
        var map = MakeMapFromAscii("test_map",
        [
            "......",
            ".##...",
            ".##...",
            "......",
            "......",
            "......",
        ]);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 0, 1, 4, 2);

        Assert.NotEmpty(path);
        for (var i = 1; i < path.Count; i++)
        {
            var (px, py) = path[i - 1];
            var (cx, cy) = path[i];
            var dx = Math.Abs(cx - px);
            var dy = Math.Abs(cy - py);
            if (dx == 1 && dy == 1)
            {
                // A legal diagonal move requires BOTH orthogonal neighbors of the step to also be
                // open - this asserts that invariant for every diagonal step actually taken.
                Assert.True(map.IsTraversalCell(px, cy), $"Illegal corner cut: ({px},{py})->({cx},{cy}) via ({px},{cy})");
                Assert.True(map.IsTraversalCell(cx, py), $"Illegal corner cut: ({px},{py})->({cx},{cy}) via ({cx},{py})");
            }
        }
    }

    [Fact]
    public void ComputePath_EveryReturnedCell_IsTraversalValid()
    {
        // Destination is deliberately NOT on the raw artifact's final row/column - pinned
        // traversal bounds (IsTraversalCell) always exclude those regardless of obstacles.
        var map = MakeMapFromAscii("test_map",
        [
            "...........",
            ".####......",
            ".#.........",
            ".#.####....",
            ".#.#..#....",
            ".#.#..#....",
            "...#..#....",
            "......#....",
            "...........",
        ]);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 0, 0, 9, 7);

        Assert.NotEmpty(path);
        Assert.All(path, cell => Assert.True(map.IsTraversalCell(cell.X, cell.Y)));
    }

    // Pinned MAX_WALKPATH (path.hpp:14) - path_search's own reconstruction fails once the STEP
    // count exceeds 32 (path.cpp:409-411). This provider's own ComputePath return value includes
    // the starting cell (see this provider's own doc comment/ReconstructPath), so 32 movement
    // steps = path.Count 33 (valid) and 33 movement steps = path.Count 34 (must fail/return empty).
    [Fact]
    public void ComputePath_Exactly32Steps_Succeeds()
    {
        var map = MakeAllWalkableMap("test_map", 40);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 0, 0, 32, 0);

        Assert.Equal(33, path.Count); // 32 steps + the starting cell.
        Assert.Equal((ushort)32, path[^1].X);
        Assert.Equal((ushort)0, path[^1].Y);
    }

    [Fact]
    public void ComputePath_Exactly33Steps_FailsEvenThoughAShorterPathWouldOtherwiseExist()
    {
        var map = MakeAllWalkableMap("test_map", 40);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        var path = pathfinder.ComputePath("test_map", 0, 0, 33, 0);

        Assert.Empty(path);
    }

    [Fact]
    public void ComputePath_UnknownMap_Throws()
    {
        var provider = new MapCollisionProvider([]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        Assert.Throws<InvalidOperationException>(() => pathfinder.ComputePath("nonexistent", 0, 0, 5, 5));
    }

    [Fact]
    public void ComputePath_OutOfBoundsCoordinates_ReturnsEmpty()
    {
        var map = MakeAllWalkableMap("test_map", 10);
        var provider = new MapCollisionProvider([map]);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);

        Assert.Empty(pathfinder.ComputePath("test_map", 0, 0, 50, 50));
    }

    // Real int_land map-cache integration: proves the pathfinder works against the ACTUAL pinned
    // map_cache.dat, not only synthetic ASCII fixtures.
    [Fact]
    public void ComputePath_RealIntLandMapCache_FindsAValidRouteBetweenTwoKnownWalkableCells()
    {
        var repository = FindRepositoryRoot();
        var mapCachePath = Path.Combine(repository, "legacy/rathena/db/map_cache.dat");
        var maps = RathenaMapCacheReader.ReadAllFromFile(mapCachePath);
        var provider = new MapCollisionProvider(maps);
        var pathfinder = new RathenaCompatibleMovementPathProvider(provider);
        provider.TryGetMap("int_land", out var map);

        // (63,69) is one of the real walkable coordinates verified in
        // PoringRandomSpawnIntegrationTests against the same pinned file.
        Assert.True(map.IsTraversalCell(63, 69));

        // Find another real walkable cell nearby to route to.
        ushort? destX = null, destY = null;
        for (var dx = -20; dx <= 20 && destX is null; dx++)
        {
            for (var dy = -20; dy <= 20; dy++)
            {
                var x = (ushort)(63 + dx);
                var y = (ushort)(69 + dy);
                if ((dx != 0 || dy != 0) && map.IsInBounds(x, y) && map.IsTraversalCell(x, y))
                {
                    destX = x;
                    destY = y;
                    break;
                }
            }
        }
        Assert.NotNull(destX);

        var path = pathfinder.ComputePath("int_land", 63, 69, destX!.Value, destY!.Value);

        Assert.NotEmpty(path);
        Assert.All(path, cell => Assert.True(map.IsTraversalCell(cell.X, cell.Y)));
    }

    // The following tests use the REAL production three-layer collision composition
    // (MapCollisionStartupLoader.Load, exactly as production startup composes it: base
    // db/map_cache.dat + optional db/import overlay + Renewal ruleset db/re overlay), not the raw
    // base cache file alone - int_land/izlude/prt_fild08/etc all genuinely need this because
    // prontera/prt_fild08 exist ONLY in the Renewal overlay, not in the base cache at all.
    private static RathenaCompatibleMovementPathProvider CreateProductionPathfinder(out IMapCollisionProvider provider)
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");
        provider = MapCollisionStartupLoader.Load([], mapCachePath, RagnarokRuleSet.Renewal);
        return new RathenaCompatibleMovementPathProvider(provider);
    }

    // Bug 2 root-cause regression: (44,54) on int_land04 is a genuine wall cell in the real pinned
    // collision data, so World rejecting a click there is source-compatible, correct behavior - not
    // an architecture bug to route around. Confirmed by direct inspection of the real data before
    // writing this test (see the PR investigation): the entire row y=54 in this x-range is blocked.
    [Fact]
    public void ComputePath_IntLand04_BlockedTutorialCell_ReturnsEmptyPath()
    {
        var pathfinder = CreateProductionPathfinder(out var provider);
        Assert.True(provider.TryGetMap("int_land04", out var map));

        Assert.False(map.IsTraversalCell(44, 54));
        Assert.True(map.IsTraversalCell(51, 60));

        var path = pathfinder.ComputePath("int_land04", 51, 60, 44, 54);

        Assert.Empty(path);
    }

    // Isolates "that specific click was blocked" from "the trigger itself is unreachable": the
    // #intro_to_izlude_d warp trigger (legacy/rathena/npc/re/warps/cities/izlude.txt:113) sits at
    // int_land04 (49,57) radius (2,2) - a genuinely different, reachable location from (44,54).
    [Fact]
    public void ComputePath_IntLand04_ReachesIntroToIzludeTrigger_FromRealisticApproach()
    {
        var pathfinder = CreateProductionPathfinder(out var provider);
        Assert.True(provider.TryGetMap("int_land04", out var map));

        // Find a walkable cell within the trigger's rectangle (center 49,57 radius 2,2).
        ushort? triggerX = null, triggerY = null;
        for (var dx = -2; dx <= 2 && triggerX is null; dx++)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                var x = (ushort)(49 + dx);
                var y = (ushort)(57 + dy);
                if (map.IsInBounds(x, y) && map.IsTraversalCell(x, y)) { triggerX = x; triggerY = y; break; }
            }
        }
        Assert.NotNull(triggerX);

        var path = pathfinder.ComputePath("int_land04", 51, 60, triggerX!.Value, triggerY!.Value);

        Assert.NotEmpty(path);
        Assert.All(path, cell => Assert.True(map.IsTraversalCell(cell.X, cell.Y)));
    }

    // Grounds the izlude_a (20,97) spawn-lock scenario (Bug 1) in real collision data too.
    [Fact]
    public void ComputePath_IzludeA_SpawnCellIsWalkable()
    {
        var pathfinder = CreateProductionPathfinder(out var provider);
        Assert.True(provider.TryGetMap("izlude_a", out var map));

        Assert.True(map.IsTraversalCell(20, 97));
        Assert.True(map.IsTraversalCell(20, 98));
    }

    // Known-good control (live scenario C): prontera -> prt_fild08 already works and must never
    // regress. Both maps exist ONLY in the Renewal ruleset overlay (db/re/map_cache.dat), not in
    // the base cache - this test only passes because CreateProductionPathfinder uses the real
    // three-layer composition, proving that composition is required, not merely convenient.
    [Fact]
    public void ComputePath_Prontera_ToPrtFild08Warp_KnownGoodControl()
    {
        var pathfinder = CreateProductionPathfinder(out var provider);
        Assert.True(provider.TryGetMap("prontera", out var prontera));
        Assert.True(provider.TryGetMap("prt_fild08", out var prtFild08));

        Assert.True(prontera.IsTraversalCell(156, 26));
        Assert.True(prtFild08.IsTraversalCell(170, 375));

        var path = pathfinder.ComputePath("prontera", 156, 26, 156, 24);

        Assert.NotEmpty(path);
        Assert.All(path, cell => Assert.True(prontera.IsTraversalCell(cell.X, cell.Y)));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
