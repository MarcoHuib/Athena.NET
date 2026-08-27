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

    public MobInstance(uint actorId, MobSpawnDefinition spawn, ushort x, ushort y)
    {
        ActorId = actorId;
        Spawn = spawn;
        _position = new MobPosition(x, y);
        _currentHp = spawn.Mob.MaxHp;
        _state = MobLifecycleState.Alive;
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
    // (Spawn.X/Y/Xs/Ys) stays the immutable pinned instruction; this is the current resolved
    // runtime cell. Returning one MobPosition value (rather than exposing separate X/Y properties)
    // means a caller physically cannot read one axis from a new position and the other from a
    // stale one across a concurrent respawn - there is only one field to read, and it is always
    // replaced as one atomic reference-copy under this instance's lock.
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
            return true;
        }
    }
}
