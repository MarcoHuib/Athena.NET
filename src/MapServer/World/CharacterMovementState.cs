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
    // Pinned ud->to_x/ud->to_y + ud->state.change_walk_target (unit.cpp:884-899): a mid-walk
    // retarget while ud->walktimer != INVALID_TIMER does NOT touch the in-flight step at all - it
    // only overwrites the desired destination and sets a flag consulted later, at the NEXT cell
    // boundary (unit_walktoxy_timer, unit.cpp:738-744). Mirrored here as a single nullable field
    // (not a queue): pinned source has exactly one to_x/to_y pair, so a second retarget before the
    // first is applied simply overwrites it - "latest wins" falls out of assignment, not merged.
    private (ushort X, ushort Y)? _pendingRetargetDestination;

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

    // Exposed for diagnostics/logging only (see MapClientSession's own "Movement retarget
    // deferred/applied" log lines) - callers must not branch gameplay logic on this beyond reading
    // it, since RequestRetarget/ConsumePendingRetarget are the only mutators.
    public (ushort X, ushort Y)? PendingRetargetDestination => _pendingRetargetDestination;

    // The cell the CURRENTLY in-flight step is walking INTO (i.e. the cell CurrentX/CurrentY will
    // become once this step completes) - diagnostics-only, same rationale as
    // PendingRetargetDestination above. Null when not moving (no next step exists).
    public (ushort X, ushort Y)? NextCell => IsMoving ? _path[_pathPosition + 1] : null;

    // Duration of the step CURRENTLY in flight (from _pathPosition to _pathPosition+1) - orthogonal
    // vs. diagonal per this type's own doc comment. 0 when not moving (no next step exists).
    private int CurrentStepMs => IsMoving ? StepDurationMs(_path[_pathPosition], _path[_pathPosition + 1], _orthogonalStepMs) : 0;

    // Next per-cell deadline, for a scheduler to sleep until (mirrors
    // CharacterStatusEffectState.NextExpiration's "null means wait indefinitely" contract). Null
    // when not moving.
    public DateTimeOffset? NextStepDueAt => IsMoving ? _stepStartedAt.AddMilliseconds(CurrentStepMs) : null;

    // The exact instant the character's CURRENT cell (CurrentX/CurrentY) was reached - i.e. the
    // real cell-boundary-crossing time AdvanceTo last advanced to (by exact multiples of a step's
    // own duration), NEVER a freshly re-sampled TimeProvider.GetUtcNow() call. A caller applying a
    // mid-walk retarget at this exact boundary (MapClientSession.ProcessDueMovementAsync) MUST seed
    // the replacement step's StartWalk with THIS value, not a second independent "now" sample -
    // re-sampling wall-clock time between AdvanceTo and StartWalk silently gifts the new step a few
    // extra milliseconds of duration every single retarget (the two calls are never exactly
    // simultaneous), which is real evidence of a slow, compounding speed-up/hop on repeated
    // mid-walk retargets - not a client-side rendering artifact. Pinned unit_walktoxy_nextcell
    // (unit.cpp:227-233) schedules its next timer from the SAME `tick` the per-cell timer callback
    // itself already received, never a fresh gettick() call partway through retarget handling.
    public DateTimeOffset CurrentCellReachedAt => _stepStartedAt;

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

    // Starts walking a FRESH path, discarding any in-flight step. Only correct to call when NOT
    // already moving (a brand-new walk from a standstill, or after AdvanceTo/ConsumePendingRetarget
    // has already brought the caller to a cell boundary) - see RequestRetarget's own doc comment
    // for the mid-walk case, which this method must NEVER be used for directly: pinned
    // unit_walktoxy (unit.cpp:884-899) does not recompute/restart the in-flight step just because a
    // new destination arrived while ud->walktimer is still running - it only overwrites ud->to_x/
    // ud->to_y and defers the actual re-path to the next cell boundary. Calling StartWalk mid-step
    // instead would reset _stepStartedAt and discard the step's already-elapsed real time, which is
    // exactly the stutter/jump-forward bug this type's own retarget API exists to avoid.
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
        _pendingRetargetDestination = null;
    }

    // Pinned unit_walktoxy's mid-walk branch (unit.cpp:889-899): "ud->to_x = x; ud->to_y = y; ...
    // if (ud->walktimer != INVALID_TIMER) { ud->state.change_walk_target = 1; return 1; }" - a
    // retarget received while a step is already in flight (IsMoving) does NOT touch _path/
    // _pathPosition/_stepStartedAt at all; it only records the desired destination for
    // ConsumePendingRetarget to apply later, at the next real cell boundary. Multiple retargets
    // before that boundary simply overwrite this one field - "latest wins", matching pinned
    // source's own plain field-assignment semantics (no queue exists in pinned ud->to_x/to_y
    // either). Callers must only call this while IsMoving is true; a caller retargeting a
    // NOT-currently-moving character should call StartWalk directly instead (matching pinned
    // source's own unit_walktoxy_sub call for that case, unit.cpp:915), since there is no in-flight
    // step whose progress would need preserving.
    public void RequestRetarget(ushort destinationX, ushort destinationY)
    {
        _pendingRetargetDestination = (destinationX, destinationY);
    }

    // Pinned unit_walktoxy_timer's own retarget-application point (unit.cpp:738-744): checked ONLY
    // once a cell boundary has actually been reached (AdvanceTo below stops advancing further the
    // instant it crosses into a cell where a retarget is pending - see that method's own doc
    // comment for why it must not silently skip past this boundary even if more elapsed time
    // remains). Returns the pending destination and clears it (one-shot consume) - the CALLER is
    // responsible for computing the real path from the character's now-current cell to this
    // destination and installing it via StartWalk; this type has no IMovementPathProvider
    // dependency of its own (see this type's own doc comment on why path computation is a separate
    // concern). Returns null when no retarget is pending, in which case the caller proceeds with
    // whatever remains of the ORIGINAL path unchanged.
    public (ushort X, ushort Y)? ConsumePendingRetarget()
    {
        var pending = _pendingRetargetDestination;
        _pendingRetargetDestination = null;
        return pending;
    }

    // Advances every cell whose travel time has elapsed by `now`, updates CurrentX/CurrentY, and
    // returns the newly crossed cells in traversal order (excluding the cell already occupied before
    // this call). Callers use these to run per-cell OnTouch/warp checks, matching rAthena's
    // per-cell npc_touch_area_allnpc/npc_touch_areanpc2 calls inside unit_walktoxy_timer - not just a
    // single check against the final destination. Each step's OWN duration (orthogonal vs.
    // diagonal) gates that step's crossing, so a diagonal step genuinely takes longer to cross than
    // an orthogonal one at the same elapsed real time.
    //
    // Stops advancing (even if `now` would allow crossing further cells) the instant it crosses into
    // a cell while a retarget is pending - pinned unit_walktoxy_timer checks change_walk_target
    // immediately after EVERY single cell arrival (unit.cpp:738), before ever considering the next
    // step (unit.cpp:744's path_pos++) - so a retarget must be applied at the FIRST cell boundary
    // reached after it was requested, never after silently continuing along the stale old path for
    // additional whole cells just because enough real time had also elapsed for them. The caller
    // (MapClientSession.ProcessDueMovementAsync) is expected to call ConsumePendingRetarget and, if
    // it returns non-null, install the replacement path via StartWalk before this instance is used
    // again - this method does not do that itself, matching this type's "no IMovementPathProvider
    // dependency" design.
    public IReadOnlyList<(ushort X, ushort Y)> AdvanceTo(DateTimeOffset now)
    {
        if (!IsMoving || _orthogonalStepMs <= 0) return [];

        List<(ushort X, ushort Y)>? crossed = null;
        while (IsMoving && now >= _stepStartedAt.AddMilliseconds(CurrentStepMs))
        {
            _stepStartedAt = _stepStartedAt.AddMilliseconds(CurrentStepMs);
            _pathPosition++;
            (crossed ??= []).Add(_path[_pathPosition]);
            if (_pendingRetargetDestination is not null) break;
        }
        return crossed ?? (IReadOnlyList<(ushort X, ushort Y)>)[];
    }

    // Pinned unit_stop_walking (unit.cpp:1695-1751, without any of its USW_MOVE_ONCE/
    // USW_MOVE_FULL_CELL/canmove_delay options this project does not yet model): an IMMEDIATE halt
    // at the character's current cell, truncating the path right there - unlike RequestRetarget
    // (which only takes effect at the NEXT cell boundary), this discards the rest of the in-flight
    // path outright, matching pinned source's own "delete_timer(ud->walktimer, ...); ud->walkpath.
    // path_len = 0" (unit.cpp:1717-1739). Used by monster combat (MobInstance.StopChase) when a
    // mob has closed to attack range and must stop advancing THIS instant, not at whatever cell
    // boundary happens to come next - unlike a player mid-walk retarget, pinned mob_ai_sub_hard's
    // own "target in range -> unit_stop_walking" (unit.cpp:2165-2166) is not itself subject to any
    // deferred-retarget semantics. A no-op when not currently moving.
    public void Stop()
    {
        if (!IsMoving) return;
        _path = [(CurrentX, CurrentY)];
        _pathPosition = 0;
        _pendingRetargetDestination = null;
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
        _pendingRetargetDestination = null;
    }
}
