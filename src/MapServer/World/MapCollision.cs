namespace Athena.Net.MapServer.World;

// Pinned rAthena's STATIC terrain cell state (legacy/rathena/src/map/map.hpp struct mapcell,
// e985006171d2eb320ee512a653f4c83aea3d81b6): exactly three bits - walkable, shootable, water -
// independent of the eight DYNAMIC runtime bits the same struct also carries (npc, basilica,
// landprotector, novending, nochat, maelstrom, icewall, nobuyingstore). This type models only the
// static half; dynamic occupancy/effects remain a MapServer runtime concern (if/when implemented)
// and are never part of an imported collision artifact.
//
// Every static cell_chk value pinned map.cpp's map_getcellp (map.cpp:3323-3395) actually computes
// is fully derivable from these three bits alone, so collapsing to one Walkable bit would discard
// real information the source needs and one byte per cell would not:
//   CELL_CHKWALL     = !walkable && !shootable
//   CELL_CHKWATER    =  water
//   CELL_CHKCLIFF    = !walkable &&  shootable
//   CELL_CHKPASS / CELL_CHKREACH     =  walkable   (CELL_NOSTACK stacking-limit build option unmodeled)
//   CELL_CHKNOPASS / CELL_CHKNOREACH = !walkable
// Raw GAT type -> bits (map_gat2cell, map.cpp:3280-3299): type 0/2/4/6 -> Walkable|Shootable;
// type 1 -> none; type 3 -> Walkable|Shootable|Water; type 5 -> Shootable only (a "gap", i.e. the
// CELL_CHKCLIFF case - snipable but not walkable).
[Flags]
public enum MapCellFlags : byte
{
    None = 0,
    Walkable = 1,
    Shootable = 2,
    Water = 4,
}

// One map's immutable static collision grid, built once by an offline importer (see
// tools/WorldDataImporter's compile-map-collision command) from a locally supplied .gat file -
// never parsed from proprietary client resources at MapServer runtime. Coordinates match the
// same (x,y) space every other MapServer position already uses; MapName is the internal
// extensionless Athena map name (the client-facing ".gat" suffix is a wire-serialization concern
// elsewhere, never part of this type - see IroMapTransitionPackets.NormalizeWireMapName).
public sealed class MapCollisionMap
{
    private readonly MapCellFlags[] _cells;

    public MapCollisionMap(string mapName, int width, int height, MapCellFlags[] cells)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Map width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Map height must be positive.");
        if (cells.Length != width * height)
            throw new ArgumentException($"Cell count {cells.Length} does not match width*height ({width * height}).", nameof(cells));

        MapName = mapName;
        Width = width;
        Height = height;
        _cells = cells;
    }

    public string MapName { get; }
    public int Width { get; }
    public int Height { get; }

    // Raw artifact bounds: every real (x,y) cell this map's data actually stores, 0 <= x < Width,
    // 0 <= y < Height. This is DELIBERATELY NOT the same range pinned rAthena's own traversal
    // gameplay check uses - map_getcellp (map.cpp:3329-3331) treats x >= xs-1 or y >= ys-1 (i.e.
    // the final row/column) as always CELL_CHKNOPASS/never-CELL_CHKREACH, regardless of that
    // cell's real stored terrain byte ("NOTE: this intentionally overrides the last row and
    // column" - map.cpp:3329). A future CELL_CHK*-equivalent/spawn-selection/pathfinding consumer
    // built on this artifact must apply that x<Width-1 / y<Height-1 restriction itself when
    // reproducing map_getcellp's gameplay semantics; IsInBounds/GetCell here intentionally do NOT
    // hide the final row/column, so the artifact keeps reporting the map's REAL dimensions and
    // real stored cell data - narrowing this type's own bounds would silently discard the last
    // row/column's genuine terrain bytes from every caller, including ones that legitimately want
    // the raw imported data (e.g. a future diagnostic/validation tool).
    public bool IsInBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    // Throws for an out-of-bounds cell rather than silently returning None/blocked - callers that
    // may legitimately probe an unverified position must check IsInBounds first (matching this
    // project's "never silently resolve a data invariant violation" convention). An out-of-bounds
    // read is a caller bug, not a normal "cell is blocked" outcome, and must not be conflated with
    // one.
    public MapCellFlags GetCell(int x, int y)
    {
        if (!IsInBounds(x, y)) throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) is outside {MapName} ({Width}x{Height}).");
        return _cells[x + y * Width];
    }

    public bool IsWalkable(int x, int y) => GetCell(x, y).HasFlag(MapCellFlags.Walkable);
    public bool IsShootable(int x, int y) => GetCell(x, y).HasFlag(MapCellFlags.Shootable);
    public bool IsWater(int x, int y) => GetCell(x, y).HasFlag(MapCellFlags.Water);

    // Centralizes pinned map_getcellp's (map.cpp:3323-3395) CELL_CHKREACH gameplay-traversal
    // semantics for THIS static collision slice, so callers (spawn-cell selection today; future
    // pathfinding/movement) never scatter their own "x >= Width - 1" boundary check next to a raw
    // IsWalkable call. Deliberately named IsTraversalCell rather than "IsReachable"/"CanReach": this
    // answers "is this ONE cell statically walkable under rAthena's traversal bounds" (pinned
    // CELL_CHKREACH), NOT "does a path exist from A to B" - a future A*/pathfinding reachability
    // concept must not be confused with, or accidentally reuse the name of, this purely per-cell
    // static check. Pinned map_getcellp explicitly excludes the final row/column from traversal
    // ("NOTE: this intentionally overrides the last row and column", map.cpp:3329) - 0 <= x <
    // Width-1 and 0 <= y < Height-1 - which is narrower than IsInBounds/GetCell's raw artifact
    // bounds above (0 <= x < Width) by design; see those members' own doc comments for why the raw
    // bounds must not silently shrink to match. CELL_CHKREACH itself reduces to plain Walkable
    // inside that narrower range (map.cpp:3375-3376) - the CELL_NOSTACK dynamic-occupancy
    // refinement on the sibling CELL_CHKPASS case is an unmodeled non-default rAthena build option,
    // matching this type's existing documented scope (see the MapCellFlags doc comment).
    public bool IsTraversalCell(int x, int y) => x >= 0 && x < Width - 1 && y >= 0 && y < Height - 1 && IsWalkable(x, y);
}

// Immutable lookup over every map this MapServer process has imported collision data for.
// Deliberately keyed by the same internal extensionless map name every other MapServer/World type
// uses (WorldMapRegistry, MobSpawnDefinition.Map, etc.) - never the client-facing ".gat" form.
// A map with no imported data is NOT the same as a map whose (x,y) cell is blocked: TryGetMap
// returns false so callers can distinguish "no collision data available for this map at all" (the
// current state for every map in this repository - see MobSpawnCellSelector.cs/
// MovementPathProvider.cs's own documented gap) from "this specific cell is non-walkable".
public interface IMapCollisionProvider
{
    bool TryGetMap(string mapName, out MapCollisionMap map);
}

// Production default while no map has imported collision data (the current state of every map in
// this repository - see MapCollisionMap's own doc comment). TryGetMap always returns false; this
// is the explicit "known-empty" case, kept distinct from a caller accidentally passing null.
public sealed class EmptyMapCollisionProvider : IMapCollisionProvider
{
    public static readonly EmptyMapCollisionProvider Instance = new();
    public bool TryGetMap(string mapName, out MapCollisionMap map) { map = null!; return false; }
}

// Simple immutable in-memory provider composed from already-loaded MapCollisionMap instances
// (e.g. read from disk by MapCollisionStartupLoader). Map-name lookup is case-insensitive-ordinal,
// matching every other map-name comparison in this codebase (WorldMapRegistry,
// MonsterRegistry.TryGetInstance, etc.).
//
// Deliberately keyed by an explicit logical-name -> map dictionary rather than deriving keys from
// each MapCollisionMap.MapName: several logical Athena map names can share exactly ONE physical
// collision resource (e.g. int_land/int_land01../04 all render the same client-side int_land.gat -
// see ai/world-data.md). The one-argument constructor below covers the common "each map is its own
// resource" case by keying on MapCollisionMap.MapName; MapCollisionStartupLoader uses the explicit
// dictionary constructor to register one loaded map under multiple logical aliases without ever
// duplicating the underlying cell array.
public sealed class MapCollisionProvider : IMapCollisionProvider
{
    private readonly IReadOnlyDictionary<string, MapCollisionMap> _maps;

    public MapCollisionProvider(IEnumerable<MapCollisionMap> maps)
        : this(maps.ToDictionary(map => map.MapName, map => map, StringComparer.OrdinalIgnoreCase))
    {
    }

    public MapCollisionProvider(IReadOnlyDictionary<string, MapCollisionMap> mapsByLogicalName)
    {
        _maps = mapsByLogicalName;
    }

    public bool TryGetMap(string mapName, out MapCollisionMap map) => _maps.TryGetValue(mapName, out map!);
}
