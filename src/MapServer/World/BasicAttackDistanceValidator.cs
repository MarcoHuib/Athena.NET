namespace Athena.Net.MapServer.World;

// Traced from pinned rAthena's path.cpp distance_math/distance_client/check_distance_client
// (path.cpp:485-517) - the CLIENT's own circular distance test, used ONLY for a player's own
// attack-command distance decision (never for monster AI, which uses the plain Chebyshev
// distance() - see path.hpp:71/path.cpp:448). Deliberately NOT abs(dx)<=range && abs(dy)<=range
// (a square test) and NOT Math.Max(abs(dx),abs(dy))<=range (Chebyshev) - the pinned comment on
// check_distance_client itself is explicit: "The client uses a circular distance instead of the
// square one."
public static class ClientDistance
{
    // Pinned distance_math (path.cpp:495-498): the plain Euclidean length of the (dx,dy) vector.
    public static double DistanceMath(int dx, int dy) => Math.Sqrt((double)dx * dx + (double)dy * dy);

    // Pinned distance_client (path.cpp:510-518): floor(max(0, sqrt(dx^2+dy^2) - 0.1)). The -0.1
    // bonus is pinned source's own documented client quirk ("Bonus factor used by client - This
    // affects even horizontal/vertical lines so they are one cell longer than expected") - e.g. an
    // orthogonal distance of exactly 1.0 becomes 1.0-0.1=0.9, floored to 0, so a Range=1 weapon
    // (check_distance_client tests distance<=range) accepts distance 1 cells away with room to
    // spare; this must be reproduced exactly, not approximated.
    public static int DistanceClient(int dx, int dy)
    {
        var tempDist = DistanceMath(dx, dy) - 0.1;
        if (tempDist < 0) tempDist = 0;
        return (int)tempDist;
    }

    // Pinned check_distance_client (path.cpp:485-490): `if(distance<0) distance=0; return
    // distance_client(dx,dy) <= distance;`. `range` clamped to >=0 exactly like pinned source's
    // own defensive clamp (a negative configured/resolved range must never make every distance
    // fail the <= comparison in some undefined way).
    public static bool CheckDistanceClient(int dx, int dy, int range)
    {
        if (range < 0) range = 0;
        return DistanceClient(dx, dy) <= range;
    }
}

// Traced from pinned rAthena's battle_check_range (battle.cpp:8215-8235) for the PC/CIRCULAR_AREA-
// undefined build this project matches (see ClientDistance's own doc comment on why the client
// path uses circular distance while everything else in pinned source defaults to Chebyshev
// unless CIRCULAR_AREA is defined, which stock rAthena does not define by default):
//   1. Different map -> false (not modeled here: this project's own MapClientSession callers
//      never call this across maps - the attacker/target map-equality check happens earlier).
//   2. PC attacker -> re-check check_distance_client_bl(src,bl,range) (redundant with the
//      caller's own BasicAttackDistanceValidator.IsInRange call, but reproduced for fidelity).
//   3. distance_bl (plain Chebyshev, path.cpp:448) < 2 -> true immediately, no path check needed -
//      this is the short-circuit that makes battle_check_range's line-of-attack test UNREACHABLE
//      for any Range<=1 weapon (Knife) whenever step 2 already passed: check_distance_client's
//      circular distance is always >= the Chebyshev distance for the same (dx,dy), so
//      check_distance_client<=1 passing implies Chebyshev<=1 too, i.e. distance_bl<2.
//   4. distance_bl > AREA_SIZE -> false (not modeled: AREA_SIZE/battle_config.area_size is a
//      server-wide view-distance constant this project has no equivalent configuration surface
//      for yet; a Range=1 weapon's distance_bl is always <=1, far under any plausible AREA_SIZE,
//      so this branch is likewise unreachable for the currently-modeled weapon).
//   5. Otherwise: path_search_long(..., CELL_CHKWALL) - a straight Bresenham-style line walk
//      (path.cpp:132-190) checking every INTERMEDIATE cell (excluding both endpoints) for
//      CELL_CHKWALL (map.cpp:3341-3343: "!cell.walkable && !cell.shootable" - a true wall, not
//      merely non-walkable water/cliff). Reproduced here against the same loaded
//      IMapCollisionProvider every other collision check in this project uses - never re-parsing
//      map_cache.dat, never a second collision data source.
public static class BasicAttackDistanceValidator
{
    // Pinned distance() with CIRCULAR_AREA undefined (path.cpp:448-453): plain Chebyshev.
    private static int ChebyshevDistance(int dx, int dy) => Math.Max(Math.Abs(dx), Math.Abs(dy));

    // Step 3-5 of pinned battle_check_range, given the caller has ALREADY verified
    // check_distance_client (step 2) separately - see this class's own doc comment for why this
    // split matches pinned source's own two-call-site structure (unit_attack_timer_sub calls
    // check_distance_client_bl directly, THEN battle_check_range, which re-checks the same thing).
    public static bool HasDirectAttackPath(IMapCollisionProvider collisionProvider, string mapName, int srcX, int srcY, int dstX, int dstY, int range)
    {
        var dx = srcX - dstX;
        var dy = srcY - dstY;
        if (ChebyshevDistance(dx, dy) < 2) return true; // No path check needed - adjacent or same cell.

        if (!collisionProvider.TryGetMap(mapName, out var map))
            throw new InvalidOperationException($"No collision data is loaded for map '{mapName}'.");

        return !LineCrossesWall(map, srcX, srcY, dstX, dstY);
    }

    // Pinned path_search_long (path.cpp:132-190): Bresenham-style line walk from (x0,y0) to
    // (x1,y1), checking every cell strictly between the two endpoints (the loop's own `(x0 != x1
    // || y0 != y1)` guard means the DESTINATION cell itself is never checked, matching pinned
    // source exactly - only intermediate cells can fail this test).
    private static bool LineCrossesWall(MapCollisionMap map, int x0, int y0, int x1, int y1)
    {
        var dx = x1 - x0;
        if (dx < 0)
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
            dx = -dx;
        }
        var dy = y1 - y0;

        int weight;
        if (dx > Math.Abs(dy)) weight = dx;
        else weight = Math.Abs(y1 - y0);
        if (weight == 0) return false; // Same cell - no intermediate cell exists.

        var wx = 0;
        var wy = 0;
        while (x0 != x1 || y0 != y1)
        {
            wx += dx;
            wy += dy;
            if (wx >= weight)
            {
                wx -= weight;
                x0++;
            }
            if (wy >= weight)
            {
                wy -= weight;
                y0++;
            }
            else if (wy < 0)
            {
                wy += weight;
                y0--;
            }

            if ((x0 != x1 || y0 != y1) && IsWall(map, x0, y0)) return true;
        }
        return false;
    }

    // Pinned CELL_CHKWALL (map.cpp:3341-3343): a cell is a wall only when it is BOTH non-walkable
    // AND non-shootable - a cliff (non-walkable but shootable) is NOT a wall for this check.
    private static bool IsWall(MapCollisionMap map, int x, int y) =>
        map.IsInBounds(x, y) && !map.IsWalkable(x, y) && !map.IsShootable(x, y);
}
