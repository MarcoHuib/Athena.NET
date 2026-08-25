namespace Athena.Net.MapServer.World;

// Authoritative per-cell walk timing/lifecycle, modeled after pinned rAthena's unit_walktoxy_timer
// (unit.cpp:542): position advances exactly one cell per CellDurationMs of REAL elapsed time,
// re-armed every cell (unit_walktoxy_nextcell, unit.cpp:180) - never an immediate jump to the
// destination. This type owns ONLY timing/lifecycle; which cells make up Path comes from an
// injected IMovementPathProvider, kept as a separate concern per that interface's own doc comment.
//
// Uses TimeProvider (caller-supplied "now") rather than real timers so movement tests are
// deterministic, matching CharacterStatusEffectState's existing "no Timer/Task.Delay per entity"
// scheduling philosophy in this codebase.
public sealed class CharacterMovementState
{
    private IReadOnlyList<(ushort X, ushort Y)> _path;
    private int _pathPosition; // Index of the cell the character currently occupies (Path[0] at construction).
    private DateTimeOffset _stepStartedAt;
    private int _cellDurationMs;

    public CharacterMovementState(string map, ushort startX, ushort startY)
    {
        Map = map;
        _path = [(startX, startY)];
        _pathPosition = 0;
        _cellDurationMs = 0;
        _stepStartedAt = DateTimeOffset.MinValue;
    }

    public string Map { get; private set; }
    public ushort CurrentX => _path[_pathPosition].X;
    public ushort CurrentY => _path[_pathPosition].Y;
    public (ushort X, ushort Y) Destination => _path[^1];
    public bool IsMoving => _pathPosition < _path.Count - 1;

    // Starts walking a new path from the character's CURRENT cell (NOT necessarily the cell any
    // earlier in-flight walk was originally heading toward). Callers MUST call AdvanceTo(now) first
    // if a walk is already in progress, matching pinned rAthena's mid-walk retarget: unit_walktoxy
    // (unit.cpp:894-899) does not recompute the path immediately when already walking - it only
    // flags change_walk_target, and the actual re-path happens later from unit_walktoxy_timer
    // (unit.cpp:738), using whatever cell the unit has ALREADY physically reached by then. This
    // method assumes that "advance to current" step already happened; it does not perform it itself,
    // so a caller that forgets to AdvanceTo first will retarget from a stale cell.
    public void StartWalk(IReadOnlyList<(ushort X, ushort Y)> path, int cellDurationMs, DateTimeOffset now)
    {
        if (path.Count == 0) throw new ArgumentException("A walk path must contain at least the current cell.", nameof(path));
        if (cellDurationMs < 0) throw new ArgumentOutOfRangeException(nameof(cellDurationMs));
        _path = path;
        _pathPosition = 0;
        _cellDurationMs = cellDurationMs;
        _stepStartedAt = now;
    }

    // Advances every cell whose travel time has elapsed by `now`, updates CurrentX/CurrentY, and
    // returns the newly crossed cells in traversal order (excluding the cell already occupied before
    // this call). Callers use these to run per-cell OnTouch/warp checks, matching rAthena's
    // per-cell npc_touch_area_allnpc/npc_touch_areanpc2 calls inside unit_walktoxy_timer - not just a
    // single check against the final destination.
    public IReadOnlyList<(ushort X, ushort Y)> AdvanceTo(DateTimeOffset now)
    {
        if (!IsMoving || _cellDurationMs <= 0) return [];

        List<(ushort X, ushort Y)>? crossed = null;
        while (IsMoving && now >= _stepStartedAt.AddMilliseconds(_cellDurationMs))
        {
            _pathPosition++;
            _stepStartedAt = _stepStartedAt.AddMilliseconds(_cellDurationMs);
            (crossed ??= []).Add(_path[_pathPosition]);
        }
        return crossed ?? (IReadOnlyList<(ushort X, ushort Y)>)[];
    }

    // Used only by warp/map-transition handling, which teleports the character outright rather than
    // walking it (no path, no per-cell timing) - mirrors the existing MapClientSession warp paths
    // that directly assign _x/_y today.
    public void Teleport(string map, ushort x, ushort y)
    {
        Map = map;
        _path = [(x, y)];
        _pathPosition = 0;
        _cellDurationMs = 0;
        _stepStartedAt = DateTimeOffset.MinValue;
    }
}
