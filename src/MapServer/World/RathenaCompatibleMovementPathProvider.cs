namespace Athena.Net.World.Runtime;

// Source-traced reproduction of pinned rAthena's A* walkpath search (path.cpp:269-432
// path_search, the non-"easy"/flag&1 branch - "We always use A* for finding walkpaths because it
// is what game client uses" per that function's own comment) against the already-loaded
// IMapCollisionProvider - never re-parsing map_cache.dat, never a second collision data source.
// Replaces UnverifiedGridLineMovementPathProvider as the production default: that placeholder
// remains available for explicit test fixtures that don't need real obstacle-aware routing (see
// its own doc comment), but it must never be the live MapServer's actual movement authority now
// that real collision data exists - production monster movement, player movement validation, and
// attack-approach/chase are all meant to share this ONE pathfinding foundation (ai/world-data.md).
//
// Traced exactly against path_search's A* branch:
//   - Cell predicate is CELL_CHKNOPASS (pinned unit_walktoxy passes this literal cell_chk value,
//     unit.cpp:265,855) - reproduced here as `!map.IsTraversalCell(x, y)` (IsTraversalCell already
//     centralizes CELL_CHKREACH/CELL_CHKPASS's shared "Walkable within pinned traversal bounds"
//     semantics - see that method's own doc comment for why raw IsInBounds must not be used).
//   - 8-directional grid: orthogonal moves cost MOVE_COST=10 (path.hpp:11), diagonal moves cost
//     MOVE_DIAGONAL_COST=14 (path.hpp:12).
//   - Diagonal corner-cutting is explicitly forbidden: a diagonal move is only allowed when BOTH
//     of its adjacent orthogonal cells are also open (path.cpp:358-408's `allowed_dirs`/`chk_dir`
//     bitmask and the `!map_getcellp(...)` guard on each diagonal `add_path` call) - "Diagonal
//     directions are only allowed if both directions around it are allowed. This is to prevent
//     cutting corner of nearby wall" (path.cpp:358-361).
//   - Heuristic is Manhattan distance scaled by MOVE_COST (path.cpp:55: "inadmissible
//     (overestimating) heuristic used by game client" - deliberately reproduced as-is, not
//     replaced with an admissible one, since matching the client's own path shape is the point).
//   - Start/destination bounds check (path.cpp:285,289) and destination-cell-blocked check
//     (path.cpp:289) both fail the search outright before any node expansion.
//   - No path found (open set exhausted, path.cpp:368-370) returns failure - never a partial or
//     approximate route.
public sealed class RathenaCompatibleMovementPathProvider(IMapCollisionProvider collisionProvider) : IMovementPathProvider
{
    private const int MoveCost = 10;
    private const int MoveDiagonalCost = 14;

    // Pinned MAX_WALKPATH (path.hpp:14) - path_search's own reconstruction rejects a path once its
    // STEP count (`len`, the number of parent-links walked back from goal to start, path.cpp:409-
    // 411 `for (it = current; it->parent != nullptr; it = it->parent, len++);` then `if (len >
    // sizeof(wpd->path)) return false;`) exceeds this. `len` counts movement directions/steps, NOT
    // cells - the pinned walkpath_data.path[MAX_WALKPATH] array holds one `directions` entry per
    // STEP, never the starting cell itself. This provider's own ComputePath return value DOES
    // include the starting cell (see its own doc comment/ReconstructPath), so the equivalent check
    // here is `path.Count - 1 > MaxWalkPath` - a 32-step path is `path.Count == 33` and valid; a
    // 33-step path is `path.Count == 34` and must fail.
    private const int MaxWalkPathSteps = 32;

    private readonly record struct Node(int X, int Y);

    public IReadOnlyList<(ushort X, ushort Y)> ComputePath(string mapName, ushort fromX, ushort fromY, ushort toX, ushort toY)
    {
        if (fromX == toX && fromY == toY) return [(fromX, fromY)];

        if (!collisionProvider.TryGetMap(mapName, out var map))
            throw new InvalidOperationException($"No collision data is loaded for map '{mapName}'.");

        // Pinned path_search checks start/destination bounds against the map's raw xs/ys (not the
        // traversal-narrowed x<xs-1/y<ys-1 range) but then rejects a blocked DESTINATION cell via
        // CELL_CHKNOPASS on that same raw-bounds check (path.cpp:285,289) - IsTraversalCell already
        // folds both the traversal-boundary exclusion and the walkability check into one predicate
        // matching CELL_CHKNOPASS's real-world effect for every cell that could ever be a valid
        // walk target, so it is used uniformly for start/destination/every expanded node here
        // rather than re-deriving the raw-bounds-only check pinned path_search performs on the
        // START cell specifically (that raw-bounds-only start check exists in pinned source only
        // to avoid rejecting a unit that is ALREADY standing on a blocked cell - not a case that
        // can arise here, since every caller of this provider only ever starts from a cell an
        // authoritative MobInstance/character position already occupies).
        if (!map.IsInBounds(fromX, fromY) || !map.IsInBounds(toX, toY)) return [];
        if (!map.IsTraversalCell(toX, toY)) return [];

        var openSet = new PriorityQueue<Node, int>();
        var gCost = new Dictionary<Node, int>();
        var parent = new Dictionary<Node, Node>();
        var closed = new HashSet<Node>();

        var start = new Node(fromX, fromY);
        var goal = new Node(toX, toY);
        gCost[start] = 0;
        openSet.Enqueue(start, Heuristic(start, goal));

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            // A node can be re-enqueued after already being closed (see TryExpand's own doc
            // comment on pinned add_path's reopening behavior) - a STALE heap entry for a node
            // whose g_cost has since improved must be skipped here rather than reprocessed, which
            // is why closed-set membership alone is not the reopening gate: only TryExpand may
            // remove a node from `closed`, and it only does so when it is actually about to
            // requeue a strictly-better entry, so a dequeued node still marked closed at this
            // point is always a stale duplicate left behind by the priority queue.
            if (closed.Contains(current)) continue;
            closed.Add(current);

            if (current == goal)
            {
                var path = ReconstructPath(parent, current, fromX, fromY);
                // Pinned path_search fails (returns false) once the reconstructed step count
                // exceeds MAX_WALKPATH (path.cpp:409-411) - see MaxWalkPathSteps' own doc comment
                // for the exact off-by-one between pinned step count and this provider's own
                // cell-inclusive path.Count. A path this long is never returned as a partial/
                // truncated route - it is a hard failure, exactly like "no path found".
                if (path.Count - 1 > MaxWalkPathSteps) return [];
                return path;
            }

            // Diagonal directions are only allowed if both cardinal directions around them are
            // open - prevents cutting the corner of a wall (path.cpp:358-361).
            var north = IsOpen(map, current.X, current.Y + 1);
            var south = IsOpen(map, current.X, current.Y - 1);
            var east = IsOpen(map, current.X + 1, current.Y);
            var west = IsOpen(map, current.X - 1, current.Y);

            if (south && east) TryExpand(current, current.X + 1, current.Y - 1, MoveDiagonalCost, goal, openSet, gCost, parent, closed, map);
            if (east) TryExpand(current, current.X + 1, current.Y, MoveCost, goal, openSet, gCost, parent, closed, map);
            if (north && east) TryExpand(current, current.X + 1, current.Y + 1, MoveDiagonalCost, goal, openSet, gCost, parent, closed, map);
            if (north) TryExpand(current, current.X, current.Y + 1, MoveCost, goal, openSet, gCost, parent, closed, map);
            if (north && west) TryExpand(current, current.X - 1, current.Y + 1, MoveDiagonalCost, goal, openSet, gCost, parent, closed, map);
            if (west) TryExpand(current, current.X - 1, current.Y, MoveCost, goal, openSet, gCost, parent, closed, map);
            if (south && west) TryExpand(current, current.X - 1, current.Y - 1, MoveDiagonalCost, goal, openSet, gCost, parent, closed, map);
            if (south) TryExpand(current, current.X, current.Y - 1, MoveCost, goal, openSet, gCost, parent, closed, map);
        }

        return []; // Open set exhausted with no route to the destination - fail cleanly.
    }

    private static bool IsOpen(MapCollisionMap map, int x, int y) =>
        map.IsInBounds(x, y) && map.IsTraversalCell(x, y);

    // Pinned add_path (path.cpp:219-244): a node already processed (open OR closed) is only
    // updated when the new g_cost is STRICTLY better. Critically, a CLOSED node whose g_cost
    // improves is explicitly reopened ("Put it in open set again", path.cpp:229-231) rather than
    // left alone - this is deliberate given path_search's own inadmissible (overestimating)
    // Manhattan*MOVE_COST heuristic (path.cpp:55), which does NOT guarantee closing a node in
    // heuristic-order also fixes its true g_cost forever the way it would under an admissible
    // heuristic. Reproduced here by simply removing the node from `closed` and re-enqueuing it -
    // the stale heap entry left behind (if any) is safely skipped by the closed-set guard in the
    // main loop above once this fresher entry is dequeued first (a strictly lower priority always
    // sorts ahead of the stale one).
    private static void TryExpand(Node current, int nx, int ny, int stepCost, Node goal,
        PriorityQueue<Node, int> openSet, Dictionary<Node, int> gCost, Dictionary<Node, Node> parent,
        HashSet<Node> closed, MapCollisionMap map)
    {
        if (!IsOpen(map, nx, ny)) return;
        var neighbor = new Node(nx, ny);

        var tentativeG = gCost[current] + stepCost;
        if (gCost.TryGetValue(neighbor, out var existingG) && existingG <= tentativeG) return;

        gCost[neighbor] = tentativeG;
        parent[neighbor] = current;
        closed.Remove(neighbor); // Reopen if this node had already been closed - see this method's own doc comment.
        openSet.Enqueue(neighbor, tentativeG + Heuristic(neighbor, goal));
    }

    // Manhattan distance scaled by MOVE_COST - the same "inadmissible but matches the client"
    // heuristic pinned path_search itself uses (path.cpp:55).
    private static int Heuristic(Node a, Node b) => MoveCost * (Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y));

    private static IReadOnlyList<(ushort X, ushort Y)> ReconstructPath(Dictionary<Node, Node> parent, Node goal, ushort fromX, ushort fromY)
    {
        var path = new List<(ushort X, ushort Y)>();
        var node = goal;
        while (parent.TryGetValue(node, out var prev))
        {
            path.Add(((ushort)node.X, (ushort)node.Y));
            node = prev;
        }
        path.Add((fromX, fromY));
        path.Reverse();
        return path;
    }
}
