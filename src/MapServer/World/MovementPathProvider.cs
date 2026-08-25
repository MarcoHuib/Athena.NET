namespace Athena.Net.MapServer.World;

// Separates WHICH cells a walk passes through from HOW LONG each cell takes (CharacterMovementState).
// Pinned rAthena keeps these as two genuinely separate concepts too: unit_walktoxy_sub calls
// path_search (path.cpp:269) to compute the cell sequence, then unit_walktoxy_nextcell/
// unit_walktoxy_timer (unit.cpp:180,542) advance one cell of that sequence per timer tick. This
// interface exists so the timing/lifecycle model (CharacterMovementState) does not need to change
// when a real path-search capability becomes available - only the provider implementation would.
public interface IMovementPathProvider
{
    // Returns the ordered cell sequence from (fromX,fromY) (inclusive, index 0) to (toX,toY)
    // (inclusive, last index) that a walk would traverse. An empty/single-cell result means no
    // movement is possible/needed.
    IReadOnlyList<(ushort X, ushort Y)> ComputePath(string mapName, ushort fromX, ushort fromY, ushort toX, ushort toY);
}

// Production placeholder. Pinned rAthena's real path_search (path.cpp:269) is A* pathfinding against
// real GAT collision data ("We always use A* for finding walkpaths because it is what game client
// uses. Easy pathfinding cuts corners of non-walkable cells, but client always walks around it." -
// path.cpp comment) - NOT a straight line, even in the common case. A* only visually degenerates to
// a direct diagonal-then-straight route (rAthena's own "easy" path variant, flag&1) when the
// intervening cells happen to be obstacle-free, which Athena cannot currently determine at all: no
// .gat/mapcache/collision data exists anywhere in this repository or the pinned legacy/rathena
// submodule (same confirmed gap as IMobSpawnCellSelector/UnverifiedFallbackMobSpawnCellSelector).
//
// This provider reuses the existing Bresenham GridLineTraversal (already used for warp/touch
// intersection) as the best available placeholder. It is NOT claimed to be rAthena/stock-client path
// parity - only that it is a reasonable, disclosed, obstacle-blind approximation until real GAT data
// exists. Do not present routes from this provider as proven pathfinding behavior.
public sealed class UnverifiedGridLineMovementPathProvider : IMovementPathProvider
{
    public IReadOnlyList<(ushort X, ushort Y)> ComputePath(string mapName, ushort fromX, ushort fromY, ushort toX, ushort toY) =>
        GridLineTraversal.Enumerate(fromX, fromY, toX, toY).ToArray();
}
