namespace Athena.Net.MapServer.World;

// Picks a spawn cell for a `MobSpawnDefinition` whose pinned declaration has
// no fixed spawn point (x=0,y=0,xs=0,ys=0 - see MobSpawnDefinition/mob.cpp
// mob_spawn). The exact rAthena behavior for this case is a map-wide
// randomized candidate search, excluding a configurable edge margin
// (battle_config.map_edge_size, default 15), retried against real GAT
// walkability (map.cpp map_search_freecell / CELL_CHKREACH), with an
// unbounded-retry whole-map fallback.
//
// Athena.NET currently has NO source of walkable-cell data anywhere in this
// repository: no .gat/mapcache files exist in this checkout, the pinned
// legacy/rathena submodule does not (and never does - client map resources
// are proprietary Gravity assets, not part of the rAthena server source),
// and MapServer tracks no per-map dimensions or collision grid at all. This
// is a genuine external-data gap, not a scope choice: reproducing
// map_search_freecell exactly would require acquiring and importing real
// client .gat/.rsw resources, which is outside this repository and this
// branch. This interface isolates that gap behind one seam so a future
// branch can supply a real GAT-backed implementation without touching
// callers, and so tests can inject deterministic positions.
public interface IMobSpawnCellSelector
{
    (ushort X, ushort Y) SelectCell(MobSpawnDefinition spawn, int instanceIndex);
}

// Production fallback used because there is no walkability data to consult
// at all (see IMobSpawnCellSelector). It reproduces only the part of
// map_search_freecell's contract that is possible without map dimensions or
// a collision grid: a map-wide-looking spread of DETERMINISTIC positions (not
// randomized - determinism keeps monster placement reproducible across
// restarts without a persisted position store) computed from the spawn's own
// declared instance count, so distinct instances do not collide with each
// other. It does NOT check walkability, map bounds, or the real edge margin,
// and MUST NOT be described as reproducing rAthena's spawn-cell selection.
public sealed class UnverifiedFallbackMobSpawnCellSelector : IMobSpawnCellSelector
{
    private const ushort BaseX = 50;
    private const ushort BaseY = 50;
    private const ushort Stride = 2;
    private const int RowWidth = 10;

    public (ushort X, ushort Y) SelectCell(MobSpawnDefinition spawn, int instanceIndex)
    {
        var row = instanceIndex / RowWidth;
        var column = instanceIndex % RowWidth;
        return ((ushort)(BaseX + column * Stride), (ushort)(BaseY + row * Stride));
    }
}
