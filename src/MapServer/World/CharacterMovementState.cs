namespace Athena.Net.MapServer.World;

// Authoritative per-cell walk timing/lifecycle, modeled after pinned rAthena's unit_walktoxy_timer
// (unit.cpp:542): position advances one cell per step's own travel time of REAL elapsed time,
// re-armed every cell (unit_walktoxy_nextcell, unit.cpp:180) - never an immediate jump to the
// destination. This type owns ONLY timing/lifecycle; which cells make up Path comes from an
// injected IMovementPathProvider, kept as a separate concern per that interface's own doc comment.
//
// Per-step duration is NOT uniform: pinned unit_walktoxy_nextcell (unit.cpp:180-247, via
// unit_get_walkpath_time's identical per-step formula, unit.cpp:1112-1127) charges
// status_get_speed(bl) for an orthogonal step and status_get_speed(bl)*MOVE_DIAGONAL_COST/MOVE_COST
// (14/10) for a diagonal one - e.g. G_PORING's WalkSpeed=400 is 400ms orthogonal but 560ms
// diagonal. Reproduced here generically (shared by player movement and monster idle movement
// alike) by deriving each step's duration from the actual (dx,dy) between consecutive path cells,
// rather than accepting one caller-supplied duration for the whole walk.
//
// Uses TimeProvider (caller-supplied "now") rather than real timers so movement tests are
// deterministic, matching CharacterStatusEffectState's existing "no Timer/Task.Delay per entity"
// scheduling philosophy in this codebase.
public sealed class CharacterMovementState
{
    private const int MoveCost = 10;
    private const int MoveDiagonalCost = 14;

    private IReadOnlyList<(ushort X, ushort Y)> _path;
    private int _pathPosition; // Index of the cell the character currently occupies (Path[0] at construction).
    private DateTimeOffset _stepStartedAt;
    private int _orthogonalStepMs;

    public CharacterMovementState(string map, ushort startX, ushort startY)
    {
        Map = map;
        _path = [(startX, startY)];
        _pathPosition = 0;
        _orthogonalStepMs = 0;
        _stepStartedAt = DateTimeOffset.MinValue;
    }

    public string Map { get; private set; }
    public ushort CurrentX => _path[_pathPosition].X;
    public ushort CurrentY => _path[_pathPosition].Y;
    public (ushort X, ushort Y) Destination => _path[^1];
    public bool IsMoving => _pathPosition < _path.Count - 1;

    // Duration of the step CURRENTLY in flight (from _pathPosition to _pathPosition+1) - orthogonal
    // vs. diagonal per this type's own doc comment. 0 when not moving (no next step exists).
    private int CurrentStepMs => IsMoving ? StepDurationMs(_path[_pathPosition], _path[_pathPosition + 1], _orthogonalStepMs) : 0;

    // Next per-cell deadline, for a scheduler to sleep until (mirrors
    // CharacterStatusEffectState.NextExpiration's "null means wait indefinitely" contract). Null
    // when not moving.
    public DateTimeOffset? NextStepDueAt => IsMoving ? _stepStartedAt.AddMilliseconds(CurrentStepMs) : null;

    // status_get_speed(bl) for an orthogonal step (unit.cpp:1122-1123); a diagonal step scales it by
    // MOVE_DIAGONAL_COST/MOVE_COST (unit.cpp:1120-1121). Integer division matches pinned source's
    // own integer arithmetic (t_tick/uint16 fields, no floating point) - e.g. 400*14/10=560 exactly
    // for G_PORING, with no fractional truncation for typical WalkSpeed values (multiples of 10).
    private static int StepDurationMs((ushort X, ushort Y) from, (ushort X, ushort Y) to, int orthogonalStepMs)
    {
        var isDiagonal = from.X != to.X && from.Y != to.Y;
        return isDiagonal ? orthogonalStepMs * MoveDiagonalCost / MoveCost : orthogonalStepMs;
    }

    // Pinned unit_get_walkpath_time (unit.cpp:1112-1127) exactly: sums each step's own
    // orthogonal/diagonal duration over the whole path - NOT `orthogonalStepMs * (path.Count - 1)`,
    // which is only correct when every step happens to be orthogonal. Exposed as a static helper
    // (rather than only an instance method) so a caller that has already computed a path but not
    // yet called StartWalk - e.g. MobInstance.TryStartIdleWalk's own pinned mob_randomwalk
    // post-success `next_walktime` rescheduling, mob.cpp:1766 - can get the walk's total real
    // duration up front without needing a live CharacterMovementState instance.
    public static int TotalWalkPathTimeMs(IReadOnlyList<(ushort X, ushort Y)> path, int orthogonalStepMs)
    {
        var total = 0;
        for (var i = 1; i < path.Count; i++)
            total += StepDurationMs(path[i - 1], path[i], orthogonalStepMs);
        return total;
    }

    // Starts walking a new path from the character's CURRENT cell (NOT necessarily the cell any
    // earlier in-flight walk was originally heading toward). Callers MUST call AdvanceTo(now) first
    // if a walk is already in progress, matching pinned rAthena's mid-walk retarget: unit_walktoxy
    // (unit.cpp:894-899) does not recompute the path immediately when already walking - it only
    // flags change_walk_target, and the actual re-path happens later from unit_walktoxy_timer
    // (unit.cpp:738), using whatever cell the unit has ALREADY physically reached by then. This
    // method assumes that "advance to current" step already happened; it does not perform it itself,
    // so a caller that forgets to AdvanceTo first will retarget from a stale cell.
    // `orthogonalStepMs` is the unit's own WalkSpeed/CellDurationMs (status_get_speed) - the base
    // unit every step's actual duration derives from, per this type's own doc comment; it is NOT
    // itself always the duration of any particular step (a diagonal step scales it).
    public void StartWalk(IReadOnlyList<(ushort X, ushort Y)> path, int orthogonalStepMs, DateTimeOffset now)
    {
        if (path.Count == 0) throw new ArgumentException("A walk path must contain at least the current cell.", nameof(path));
        if (orthogonalStepMs < 0) throw new ArgumentOutOfRangeException(nameof(orthogonalStepMs));
        _path = path;
        _pathPosition = 0;
        _orthogonalStepMs = orthogonalStepMs;
        _stepStartedAt = now;
    }

    // Advances every cell whose travel time has elapsed by `now`, updates CurrentX/CurrentY, and
    // returns the newly crossed cells in traversal order (excluding the cell already occupied before
    // this call). Callers use these to run per-cell OnTouch/warp checks, matching rAthena's
    // per-cell npc_touch_area_allnpc/npc_touch_areanpc2 calls inside unit_walktoxy_timer - not just a
    // single check against the final destination. Each step's OWN duration (orthogonal vs.
    // diagonal) gates that step's crossing, so a diagonal step genuinely takes longer to cross than
    // an orthogonal one at the same elapsed real time.
    public IReadOnlyList<(ushort X, ushort Y)> AdvanceTo(DateTimeOffset now)
    {
        if (!IsMoving || _orthogonalStepMs <= 0) return [];

        List<(ushort X, ushort Y)>? crossed = null;
        while (IsMoving && now >= _stepStartedAt.AddMilliseconds(CurrentStepMs))
        {
            _stepStartedAt = _stepStartedAt.AddMilliseconds(CurrentStepMs);
            _pathPosition++;
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
        _orthogonalStepMs = 0;
        _stepStartedAt = DateTimeOffset.MinValue;
    }
}
