namespace Athena.Net.MapServer.World;

public enum MobLifecycleState { Alive, Dead }

// One resolved runtime (x,y) cell. Deliberately a single value type (not two independently
// readable properties) so every consumer that needs a monster's current position takes ONE
// logical snapshot rather than two separate field reads that could observe different points in
// time across a respawn (see MobInstance.GetPosition's own doc comment).
public readonly record struct MobPosition(ushort X, ushort Y);

// One runtime monster instance. Mutable current HP/lifecycle/position only
// live here, never in the immutable generated MobDefinition/MobSpawnDefinition.
public sealed class MobInstance
{
    private readonly Lock _gate = new();
    private uint _currentHp;
    private MobLifecycleState _state;
    private long _deadUntilTimestamp;
    private MobPosition _position;
    // Idle-walk scheduling/movement state - see TryStartIdleWalk/AdvanceMovement's own doc
    // comments. `_nextIdleWalkTimestamp` mirrors pinned mob_data.next_walktime (mob.hpp) exactly:
    // 0 means "not yet initialized" (pinned mob_randomwalk's own `INVALID_TIMER` sentinel check,
    // mob.cpp:1681), matching that this instance has never been considered for an idle walk yet.
    private long _nextIdleWalkTimestamp;
    // Reuses CharacterMovementState as the SAME per-cell walk timing/lifecycle model player
    // movement already uses (see that type's own doc comment) - mob idle movement and player
    // movement share one timing/lifecycle mechanism, only the path SOURCE (idle AI vs. player
    // click) and the collision-backed path COMPUTATION (RathenaCompatibleMovementPathProvider,
    // shared by both) differ. Never null after construction; a freshly constructed/respawned
    // instance's movement state simply has an empty path (IsMoving=false) until a walk starts.
    private CharacterMovementState _movement;

    public MobInstance(uint actorId, MobSpawnDefinition spawn, ushort x, ushort y)
    {
        ActorId = actorId;
        Spawn = spawn;
        _position = new MobPosition(x, y);
        _currentHp = spawn.Mob.MaxHp;
        _state = MobLifecycleState.Alive;
        _movement = new CharacterMovementState(spawn.Map, x, y);
    }

    // Constructs an instance that could NOT be placed at creation time - the source-backed random
    // spawn search exhausted its attempt budget (see IMobSpawnCellSelector.TrySelectCell) with no
    // fallback available, matching pinned mob_spawn's own "search failed, reschedule via
    // mob_delayspawn and try again later" behavior (mob.cpp:1152-1159) rather than pinned rAthena
    // treating this as a fatal configuration error. Deliberately reuses the EXISTING Dead +
    // scheduled-respawn machinery instead of introducing a third lifecycle state or a separate
    // "pending" scheduler: the instance starts Dead with HP 0 and a respawn already due
    // immediately (`dueTimestamp` should be the caller's current time), so the very next
    // MonsterRegistry.ProcessDueRespawns sweep retries it through the normal TryRespawn path - a
    // caller cannot tell this apart from "a monster that died and is waiting to respawn" because,
    // by design, it is the same state.
    public static MobInstance CreatePending(uint actorId, MobSpawnDefinition spawn, long dueTimestamp)
    {
        var instance = new MobInstance(actorId, spawn, x: 0, y: 0);
        instance._currentHp = 0;
        instance._state = MobLifecycleState.Dead;
        instance._deadUntilTimestamp = dueTimestamp;
        return instance;
    }

    public uint ActorId { get; }
    public MobSpawnDefinition Spawn { get; }
    public string Map => Spawn.Map;

    // The single, atomic way to read a monster's current runtime cell: a random-spawn declaration
    // (MobSpawnDefinition X=0,Y=0,Xs=0,Ys=0 - see IMobSpawnCellSelector) picks a FRESH valid cell
    // on every respawn, matching pinned mob_spawn re-running map_search_freecell on every call
    // rather than reusing the originally resolved coordinate - the declaration itself
    // (Spawn.X/Y/Xs/Ys) stays the immutable pinned instruction. `_position` is kept synchronized
    // with `_movement`'s own current cell on every AdvanceMovement/TryStartIdleWalk/TryRespawn call
    // (see those methods' own doc comments), so it is always correct to read directly here - during
    // a walk this is the monster's actual current traversed cell, never the walk's final
    // destination and never an instantaneous jump. Returning one MobPosition value (rather than
    // exposing separate X/Y properties) means a caller physically cannot read one axis from a new
    // position and the other from a stale one across a concurrent respawn/movement update - there
    // is only one field to read, and it is always replaced as one atomic reference-copy under this
    // instance's lock.
    public MobPosition GetPosition() { lock (_gate) return _position; }

    public uint CurrentHp { get { lock (_gate) return _currentHp; } }
    public MobLifecycleState State { get { lock (_gate) return _state; } }
    public bool IsAlive => State == MobLifecycleState.Alive;

    // Applies damage and reports whether THIS call caused the Alive->Dead
    // transition (never true twice for the same death - the state check and
    // the mutation happen under one lock, so two concurrent lethal hits
    // cannot both observe "still alive").
    public (uint HpBefore, uint HpAfter, bool KilledByThisHit) ApplyDamage(uint damage)
    {
        lock (_gate)
        {
            var before = _currentHp;
            if (_state != MobLifecycleState.Alive) return (before, before, false);
            var after = damage >= before ? 0u : before - damage;
            _currentHp = after;
            var killed = after == 0;
            if (killed) _state = MobLifecycleState.Dead;
            return (before, after, killed);
        }
    }

    // Idempotent: only the first caller after death schedules a respawn (via
    // the returned bool); a second call while already dead returns false and
    // changes nothing, so a respawn cannot be scheduled twice for one death.
    public bool TryScheduleRespawn(long dueTimestamp)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Dead || _deadUntilTimestamp != 0) return false;
            _deadUntilTimestamp = dueTimestamp;
            return true;
        }
    }

    // Returns true and resets to Alive/full HP (and, for a random-spawn declaration, a FRESH valid
    // position - see GetPosition's own doc comment) exactly once when `now` has reached the
    // scheduled respawn time; false otherwise (including when no respawn is scheduled, or it
    // already fired) - in which case `selectPosition` is NOT invoked at all (see the "before due
    // time" pre-check below), so a caller's selector is never called speculatively.
    // `selectPosition` returns `false` for a genuine TEMPORARY spawn-attempt failure (the
    // source-backed random search exhausted its attempt budget without finding a valid cell - see
    // IMobSpawnCellSelector.TrySelectCell) - this is expected/retryable, matching pinned
    // mob_spawn's own "if area/whole-map search failed, reschedule via mob_delayspawn and try
    // again later" behavior (mob.cpp:1152-1159): TryRespawn returns false and LEAVES the instance
    // Dead with its existing scheduled-respawn state untouched, so MonsterRegistry's normal
    // ProcessDueRespawns sweep will simply try this same instance again on its next call - no new
    // timer/scheduler is introduced for this retry path.
    //
    // Callers pass a closure over the real IMobSpawnCellSelector/spawn definition
    // (MonsterRegistry.ProcessDueRespawns) rather than this type depending on that interface
    // directly, keeping MobInstance free of any collision-provider/selector dependency. The
    // selector call happens BEFORE the lock is taken (it only reads Spawn/collision data, never
    // this instance's mutable state) so the lock is held only for the atomic state+position
    // transition itself.
    public bool TryRespawn(long now, Func<(bool Success, MobPosition Position)> selectPosition)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Dead || _deadUntilTimestamp == 0 || now < _deadUntilTimestamp) return false;
        }

        var (success, position) = selectPosition();
        if (!success) return false;

        // Re-verify under the lock rather than trusting the pre-check above: MonsterRegistry only
        // ever calls TryRespawn from one single-threaded ProcessDueRespawns sweep in practice, but
        // re-checking here costs nothing and means this method's own correctness never depends on
        // that caller discipline - a concurrent caller can never observe (or cause) a state
        // transition this method didn't itself just validate.
        lock (_gate)
        {
            if (_state != MobLifecycleState.Dead || _deadUntilTimestamp == 0 || now < _deadUntilTimestamp) return false;
            _state = MobLifecycleState.Alive;
            _currentHp = Spawn.Mob.MaxHp;
            _deadUntilTimestamp = 0;
            _position = position;
            // Reset movement state entirely on respawn: any in-flight walk from the PREVIOUS life
            // must never continue to mutate a respawned instance's position (an old scheduled
            // movement event must not move a respawned instance to where the dead instance was
            // walking toward). `_nextIdleWalkTimestamp = 0` resets to the same "not yet
            // initialized" state a freshly-constructed instance starts in, so idle AI treats a
            // respawned instance exactly like a brand new spawn (matching pinned mob_spawn, which
            // re-initializes next_walktime via the same INVALID_TIMER path for both a genuinely new
            // spawn and a respawn - mob.cpp:1134-1143 calls mob_spawn for both).
            _movement = new CharacterMovementState(Map, position.X, position.Y);
            _nextIdleWalkTimestamp = 0;
            return true;
        }
    }

    // Pinned mob_randomwalk's idle-walk-due check (mob.cpp:1673-1690), reproduced as a pure
    // "is it time, and if so what candidate cells should the caller try" query - this method does
    // NOT itself run the 15x15 candidate search or call the pathfinder; see
    // MonsterRuntime.ProcessIdleMovement for why that responsibility lives at the scheduler level
    // (it needs the real IMapCollisionProvider/IMovementPathProvider, which MobInstance
    // deliberately has no dependency on, matching how TryRespawn's `selectPosition` callback keeps
    // MobInstance free of IMobSpawnCellSelector too).
    //
    // Returns true only when: the mob is Alive, is not currently mid-walk (pinned mob_randomwalk
    // never interrupts an in-progress walk to start another - it only runs its search when
    // `!IsMoving`), and `now` has reached `_nextIdleWalkTimestamp`. On the VERY FIRST call for a
    // freshly spawned/respawned instance (`_nextIdleWalkTimestamp == 0`, pinned mob.cpp:1681's
    // `next_walktime == INVALID_TIMER` case), this method does NOT authorize a walk yet - it only
    // initializes the timestamp to `now + jitter + MIN_RANDOMWALKTIME` and returns false, exactly
    // matching pinned mob_randomwalk's own "initialize next_walktime and return 1 without walking"
    // first-call behavior (mob.cpp:1680-1684; that pinned `return 1` means "the AI tick handled
    // this mob successfully", not "a walk started").
    public bool IsIdleWalkDue(long now, Func<long> randomJitterMs)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Alive || _movement.IsMoving) return false;

            if (_nextIdleWalkTimestamp == 0)
            {
                _nextIdleWalkTimestamp = now + randomJitterMs() + MinRandomWalkTimeMs;
                return false;
            }

            return now >= _nextIdleWalkTimestamp;
        }
    }

    // MIN_RANDOMWALKTIME (mob.hpp:39) - the pinned minimum delay before an idle mob's NEXT random
    // walk consideration, on top of which pinned mob.cpp adds `rnd()%1000` jitter both when
    // initializing next_walktime (mob.cpp:1682) and after a walk completes (mob.cpp:1766).
    internal const long MinRandomWalkTimeMs = 4000;

    // Pinned mob_ai_sub_hard's own post-failure rescheduling for a mob_randomwalk call that
    // returned false (mob.cpp:2058-2066): "if (md->next_walktime < md->ud.canmove_tick)
    // next_walktime = ud.canmove_tick; else next_walktime = tick + rnd()%1000". The
    // `ud.canmove_tick` branch (a temporary movement-lock expiry, e.g. from stun/knockback) is NOT
    // modeled by this project's MobInstance - there is no equivalent movement-lock concept here yet
    // (out of scope for this idle-movement slice, same boundary as aggro/attack AI) - so this always
    // takes the `tick + rnd()%1000` branch, which is also pinned source's own actual behavior for
    // every mob that ISN'T currently movement-locked (the overwhelmingly common case this project's
    // idle-walk slice cares about). Called by MonsterRuntime when NO candidate destination AND real
    // path both succeeded this due-tick, so a stuck-in-a-corner or momentarily unreachable mob is
    // retried again soon rather than being stuck until its much longer post-success reschedule.
    public void RescheduleAfterFailedIdleWalk(long now, Func<long> jitterMs)
    {
        lock (_gate)
        {
            _nextIdleWalkTimestamp = now + jitterMs();
        }
    }

    // Starts an idle walk along an already-computed path (the caller - MonsterRuntime - is
    // responsible for running the pinned 15x15 candidate search and RathenaCompatibleMovementPathProvider;
    // see IsIdleWalkDue's own doc comment for why that split exists). `orthogonalStepMs` is the
    // mob's own WalkSpeed - CharacterMovementState derives each individual step's actual duration
    // (orthogonal vs. diagonal) from it internally; see that type's own doc comment. `jitterMs` is
    // caller-injected (rather than this method calling Random directly) so tests can drive the
    // exact pinned `rnd()%1000` jitter deterministically. Does nothing (returns false) if the mob
    // died or started walking via another path between IsIdleWalkDue returning true and this call -
    // re-validated here under the same lock rather than trusted from the caller's earlier check.
    public bool TryStartIdleWalk(IReadOnlyList<(ushort X, ushort Y)> path, int orthogonalStepMs, long now, DateTimeOffset nowOffset, Func<long> jitterMs)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Alive || _movement.IsMoving) return false;
            _movement.StartWalk(path, orthogonalStepMs, nowOffset);
            _position = new MobPosition(_movement.CurrentX, _movement.CurrentY);
            // Pinned mob_randomwalk's own post-walk-start rescheduling (mob.cpp:1766):
            // "next_walktime = tick + rnd()%1000 + MIN_RANDOMWALKTIME + unit_get_walkpath_time" -
            // the NEXT idle-walk consideration is scheduled for after this walk's own expected
            // duration completes, not from `now`. Uses CharacterMovementState.TotalWalkPathTimeMs
            // (the EXACT pinned unit_get_walkpath_time sum over each step's own orthogonal/diagonal
            // duration) rather than a uniform orthogonalStepMs*(path.Count-1) approximation, which
            // undercounts any walk containing a diagonal step.
            _nextIdleWalkTimestamp = now + jitterMs() + MinRandomWalkTimeMs + CharacterMovementState.TotalWalkPathTimeMs(path, orthogonalStepMs);
            return true;
        }
    }

    // Advances this instance's in-progress walk by whatever whole cells have elapsed by `now`
    // (CharacterMovementState.AdvanceTo - see that type's own doc comment), keeping `_position`
    // synchronized with the walk's current cell on every call. A dead instance's movement is never
    // advanced (death must stop movement immediately - ApplyDamage's Alive->Dead transition does
    // not itself clear `_movement`, but this guard means a walk in progress at the moment of death
    // simply stops being advanced from here on, which is the observable effect required). Returns
    // the newly crossed cells (possibly empty) for a caller that wants to know exactly which cells
    // were just crossed (e.g. future per-cell trigger checks) - the current slice does not use
    // this beyond updating `_position`.
    public IReadOnlyList<(ushort X, ushort Y)> AdvanceMovement(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Alive) return [];
            var crossed = _movement.AdvanceTo(now);
            if (crossed.Count > 0) _position = new MobPosition(_movement.CurrentX, _movement.CurrentY);
            return crossed;
        }
    }

    // Read-only snapshot of movement state for a scheduler/wire-notification caller that needs to
    // know whether this instance is currently walking and when its next per-cell step is due,
    // without needing its own duplicate CharacterMovementState. Matches
    // CharacterMovementState.NextStepDueAt's own "null means not moving" contract.
    public bool IsWalking { get { lock (_gate) return _movement.IsMoving; } }
    public DateTimeOffset? NextMovementStepDueAt { get { lock (_gate) return _movement.NextStepDueAt; } }
    public (ushort X, ushort Y) MovementDestination { get { lock (_gate) return _movement.Destination; } }
}
