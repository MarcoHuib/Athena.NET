namespace Athena.Net.MapServer.World;

public enum MobLifecycleState { Alive, Dead }

// Subset of pinned e_mob_skill_state (mob.hpp MSS_*) this project's monster-engagement slice
// actually needs. MSS_IDLE = no combat target (mob_randomwalk eligible). MSS_RUSH = "Mob
// following a player after being attacked" (mob.hpp:101) - target acquired, chasing because out
// of attack range. MSS_BERSERK = "Aggressive mob attacking" (mob.hpp:99) - actually reused here
// for "target acquired AND currently in/entering attack range", matching unit_attack's own
// unconditional mob_setstate(*md, MSS_BERSERK) whenever an attack is issued (unit.cpp:2982-2983),
// regardless of whether the mob's own aggression mode is MD_AGGRESSIVE. Deliberately omits
// MSS_WALK/MSS_LOOT/MSS_ANGRY/MSS_FOLLOW - none of pinned mob_can_changetarget's behavior for
// those states is reachable by a passive (non-MD_AGGRESSIVE, non-looter-relevant) mob like
// G_PORING in this slice; see mob_can_changetarget's own switch (mob.cpp:1240-1261) for why only
// MSS_RUSH's "requires MD_CHANGETARGETCHASE to steal target" branch is reachable here.
public enum MobCombatState { Idle, Rush, Berserk }

// Immutable snapshot of a monster's combat engagement, returned by MobInstance so callers (the
// monster combat domain service) can make source-backed decisions without holding MobInstance's
// own lock or reaching into its private fields. Mirrors reading md->target_id/md->state.skillstate
// as one atomic pair (pinned source reads these as plain struct fields with no such atomicity
// concern; MobInstance's lock-per-call model requires this project to snapshot them together
// instead, matching MobPosition's own "one atomic read" rationale).
public readonly record struct MobEngagement(uint? TargetAccountId, MobCombatState State);

// One resolved runtime (x,y) cell. Deliberately a single value type (not two independently
// readable properties) so every consumer that needs a monster's current position takes ONE
// logical snapshot rather than two separate field reads that could observe different points in
// time across a respawn (see MobInstance.GetPosition's own doc comment).
public readonly record struct MobPosition(ushort X, ushort Y);

// Distinguishes a MobInstance's CURRENT life from a prior one that ended in death - a plain `long`
// wrapped in its own type so callers cannot accidentally compare it against an unrelated numeric
// ID (ActorId, AccountId, etc.) by mistake. Pure domain value: lives in the same file/namespace as
// MobInstance itself (Athena.Net.MapServer.World, file-linked unmodified into Athena.World.Monsters)
// so BOTH MapServer and World read/compare the identical representation with no dependency in
// either direction on Athena.World.Contracts' own WorldMonsterIncarnationId wire type - the World
// grain boundary (WorldMonsterMapSimulation.ToWireInstance) is the ONLY place that ever converts
// between the two, exactly like every other MobInstance-to-WorldXxx projection.
//
// Starts at 1 for a freshly constructed instance (never 0 - avoids colliding with an
// uninitialized/default(long) sentinel anywhere this value might accidentally flow through).
// Incremented exactly once per successful Dead->Alive transition (see MobInstance.TryRespawn) -
// never on death itself, never on merely scheduling or attempting a respawn.
public readonly record struct MonsterIncarnationId(long Value)
{
    public static readonly MonsterIncarnationId First = new(1);
    public MonsterIncarnationId Next() => new(Value + 1);
}

// One runtime monster instance. Mutable current HP/lifecycle/position only
// live here, never in the immutable generated MobDefinition/MobSpawnDefinition.
//
// Implements IMonsterActorView directly - see that interface's own doc comment for why its
// members are exactly this narrow (position/identity/movement only, no CurrentHp/NextAttackAt).
public sealed class MobInstance : IMonsterActorView
{
    private readonly Lock _gate = new();
    private uint _currentHp;
    private MobLifecycleState _state;
    private long _deadUntilUtcTicks;
    private MobPosition _position;
    private MonsterIncarnationId _incarnationId = MonsterIncarnationId.First;
    // Idle-walk scheduling/movement state - see TryStartIdleWalk/AdvanceMovement's own doc
    // comments. `_nextIdleWalkAt` mirrors pinned mob_data.next_walktime (mob.hpp) exactly: null
    // means "not yet initialized" (pinned mob_randomwalk's own `INVALID_TIMER` sentinel check,
    // mob.cpp:1681), matching that this instance has never been considered for an idle walk yet.
    //
    // DateTimeOffset (not a raw `long`) is deliberate: an earlier revision passed `DateTimeOffset.
    // UtcTicks` (100-nanosecond ticks) into these fields while adding raw MILLISECOND constants
    // (MinRandomWalkTimeMs=4000, mob_db AttackDelay=1872) directly to them - a real unit-mismatch
    // bug (4000 ticks = 0.4ms, not 4000ms = 4s) that would have let a mob's idle-walk/attack timers
    // fire on the very next 100ms world tick instead of after their real pinned delay. Using
    // DateTimeOffset + AddMilliseconds throughout makes that class of bug impossible to reintroduce
    // - every caller adds an explicit millisecond duration to an explicit instant, never a bare
    // numeric field whose unit depends on which caller populated it.
    private DateTimeOffset? _nextIdleWalkAt;
    // Reuses CharacterMovementState as the SAME per-cell walk timing/lifecycle model player
    // movement already uses (see that type's own doc comment) - mob idle movement and player
    // movement share one timing/lifecycle mechanism, only the path SOURCE (idle AI vs. player
    // click) and the collision-backed path COMPUTATION (RathenaCompatibleMovementPathProvider,
    // shared by both) differ. Never null after construction; a freshly constructed/respawned
    // instance's movement state simply has an empty path (IsMoving=false) until a walk starts.
    private CharacterMovementState _movement;
    // Pinned md->target_id (mob.hpp) - the account ID of the player currently locked as this
    // mob's combat target, or null when genuinely idle (mob.cpp:1655-1658's target_id=0). Never
    // read/written outside this instance's own lock; the monster combat domain service only ever
    // observes it via the Engagement snapshot, never mutates it directly (matching how
    // MonsterCombatCoordinator never mutates HP directly either - see ApplyDamage's own doc
    // comment for that precedent).
    private uint? _targetAccountId;
    private MobCombatState _combatState = MobCombatState.Idle;
    // Pinned md->attackabletime / status->adelay (mob_db AttackDelay) - the next instant at which
    // this mob's own attack timer may fire again (unit_attack_timer_sub's own ud->attacktimer
    // re-arm, unit.cpp:3337). Null means "no attack in flight / delay elapsed", matching
    // _nextIdleWalkAt's own "null = not scheduled" sentinel convention on this same type. See that
    // field's own doc comment for why this is a DateTimeOffset, not a raw `long` tick/millisecond
    // value.
    private DateTimeOffset? _nextAttackAt;

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
    // immediately (`dueUtcTicks` should be the caller's current time), so the very next
    // MonsterRegistry.ProcessDueRespawns sweep retries it through the normal TryRespawn path - a
    // caller cannot tell this apart from "a monster that died and is waiting to respawn" because,
    // by design, it is the same state.
    public static MobInstance CreatePending(uint actorId, MobSpawnDefinition spawn, long dueUtcTicks)
    {
        var instance = new MobInstance(actorId, spawn, x: 0, y: 0);
        instance._currentHp = 0;
        instance._state = MobLifecycleState.Dead;
        instance._deadUntilUtcTicks = dueUtcTicks;
        return instance;
    }

    public uint ActorId { get; }
    public MobSpawnDefinition Spawn { get; }
    public string Map => Spawn.Map;

    // IMonsterActorView's own static-mob-data passthroughs - deliberately reading through
    // Spawn.Mob rather than duplicating these fields on MobInstance itself, exactly like Map above.
    int IMonsterActorView.MobId => Spawn.Mob.Id;
    string IMonsterActorView.Name => Spawn.Mob.Name;
    int IMonsterActorView.WalkSpeed => Spawn.Mob.WalkSpeed;

    // The CURRENT life's incarnation - see MonsterIncarnationId's own doc comment. Locked read for
    // the same reason every other mutable field on this type is (TryRespawn increments this under
    // the identical lock as the rest of its atomic Dead->Alive transition, so a torn read here is
    // impossible).
    public MonsterIncarnationId IncarnationId { get { lock (_gate) return _incarnationId; } }

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

    // One atomic read of target/state together - see MobEngagement's own doc comment for why this
    // must be a single snapshot rather than two separate property reads.
    public MobEngagement Engagement { get { lock (_gate) return new MobEngagement(_targetAccountId, _combatState); } }
    public bool HasActiveTarget { get { lock (_gate) return _targetAccountId is not null; } }

    // Pinned mob_set_attacked_id + the target-change branch of mob_ai_sub_hard (mob.cpp:1936-1995):
    // establishes `attackerAccountId` as this mob's target, mirroring md->attacked_id ->
    // md->target_id promotion. This project calls it directly and immediately from
    // MonsterCombatCoordinator.Attack rather than reproducing pinned source's own two-phase
    // walk-delay-timer deferral (battle_damage schedules mob_attacked via
    // add_timer(tick+delay,...), which only THEN calls mob_set_attacked_id) - that deferral exists
    // in pinned source purely so a monster's attacked-reaction is delayed by its own walk-delay
    // (battle_calc_walkdelay), a sub-cell-duration timing nuance this slice does not model; the
    // OBSERVABLE target-acquisition outcome (attacker becomes target, mob leaves idle-walk
    // eligibility) is reproduced exactly, just without that intermediate scheduling hop.
    //
    // Mirrors mob_can_changetarget's own switch (mob.cpp:1229-1262) narrowed to the two states this
    // slice's mob AI can actually be in: Idle always accepts a new target (matches MSS_IDLE's
    // `return 1` case); Rush only accepts a DIFFERENT attacker as a replacement target if the
    // mob's own `mode` has MD_CHANGETARGETCHASE (mob_can_changetarget's MSS_RUSH case: "return
    // (mode&MD_CHANGETARGETCHASE)") - G_PORING's real generated mode LACKS this bit (mob.cpp
    // Ai=02's raw mask has no 0x2000), so a second attacker while G_PORING is already chasing the
    // first is correctly ignored, matching item 6's own traced acceptance criterion, WITHOUT any
    // mob-ID special case: this is `mode`-driven, not hardcoded per caller. Berserk (mob is
    // in/entering attack range) follows the pinned MSS_BERSERK case instead, gated on
    // MD_CHANGETARGETMELEE (mob.cpp:1242) - also absent from G_PORING's mode. The SAME attacker
    // re-hitting an already-locked target is always accepted regardless of mode (pinned
    // mob.cpp:1939: "md->attacked_id == md->target_id" is the "rude attacked" check, never a
    // target change - modeled here as a no-op success, since there is nothing to change).
    public bool TryAcquireTarget(uint attackerAccountId, MobMode mode)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Alive) return false;
            if (_targetAccountId is null || _targetAccountId == attackerAccountId)
            {
                _targetAccountId = attackerAccountId;
                if (_combatState == MobCombatState.Idle) _combatState = MobCombatState.Rush;
                return true;
            }
            var allowChangeTarget = _combatState == MobCombatState.Berserk
                ? mode.HasFlag(MobMode.ChangeTargetMelee)
                : mode.HasFlag(MobMode.ChangeTargetChase); // Rush (the only other state a locked target can coexist with).
            if (!allowChangeTarget) return false;
            _targetAccountId = attackerAccountId;
            return true;
        }
    }

    // Pinned mob_unlocktarget (mob.cpp:1627-1669), narrowed to this slice's own state machine: the
    // pinned unit_stop_attack + MSS_IDLE transition + next_walktime reschedule (mob.cpp:1647-1652).
    // `jitterMs`/`now` reschedule the NEXT idle-walk consideration exactly like pinned source's own
    // "tick+rnd()%1000+MIN_RANDOMWALKTIME" (mob.cpp:1652) - unlocking a target must not leave the
    // mob eligible to randomly walk on the very next tick, matching a genuine target loss's real
    // pinned cooldown rather than resuming idle behavior instantly. A no-op (returns false) when
    // there was no target to unlock, so a caller doesn't need its own "was there a target" guard
    // before calling this.
    public bool TryUnlockTarget(DateTimeOffset now, Func<long> jitterMs)
    {
        lock (_gate)
        {
            if (_targetAccountId is null) return false;
            _targetAccountId = null;
            _combatState = MobCombatState.Idle;
            _nextAttackAt = null;
            _nextIdleWalkAt = now.AddMilliseconds(jitterMs() + MinRandomWalkTimeMs);
            return true;
        }
    }

    // Pinned unit_attack's own unconditional mob_setstate(*md, MSS_BERSERK) whenever an attack is
    // issued (unit.cpp:2982-2983) - called by the monster combat domain service once it decides
    // this tick results in an attack rather than a chase. Idempotent/safe to call every attacking
    // tick; does nothing to _targetAccountId (a state transition alone never changes who the
    // target is).
    public void EnterAttackState()
    {
        lock (_gate)
        {
            if (_targetAccountId is not null) _combatState = MobCombatState.Berserk;
        }
    }

    // Pinned unit_walktobl's own mob_setstate(md, MSS_RUSH) whenever a chase-walk is (re)issued
    // against the current target (unit.cpp:992-995) - called by the monster combat domain service
    // once it decides this tick results in a chase rather than an attack.
    public void EnterChaseState()
    {
        lock (_gate)
        {
            if (_targetAccountId is not null) _combatState = MobCombatState.Rush;
        }
    }

    // Pinned unit_attack_timer_sub's own ud->attacktimer re-arm (unit.cpp:3337:
    // "add_timer(ud->attackabletime,unit_attack_timer,...)") - the next instant at which this mob
    // may attack again. Null means no attack has been performed yet (or the delay already elapsed)
    // - same "null = not scheduled" convention as NextMovementStepDueAt.
    public DateTimeOffset? NextAttackAt { get { lock (_gate) return _nextAttackAt; } }

    public void ScheduleNextAttack(DateTimeOffset dueAt)
    {
        lock (_gate) { _nextAttackAt = dueAt; }
    }

    // Pinned unit_walktoxy's mid-walk retarget branch (unit.cpp:884-899), reused here for monster
    // chase exactly as MapClientSession already reuses it for player movement (see
    // CharacterMovementState.RequestRetarget's own doc comment) - a chase re-issued while the mob is
    // already mid-cell must defer to the next cell boundary rather than resetting the in-flight
    // step's elapsed progress. Returns false (does nothing) if the mob is dead or not currently
    // moving - the caller (monster combat domain service) is expected to call StartWalk directly
    // for the not-moving case instead, matching CharacterMovementState.RequestRetarget's own
    // "caller must only call this while IsMoving" contract.
    public bool TryRetargetChase(ushort destinationX, ushort destinationY)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Alive || !_movement.IsMoving) return false;
            _movement.RequestRetarget(destinationX, destinationY);
            return true;
        }
    }

    // Starts (or, per StartWalk's own contract, replaces a NOT-currently-moving walk with) a fresh
    // chase path toward the target - used by the monster combat domain service's Chase decision
    // when the mob is not already mid-walk (TryRetargetChase's counterpart for that case). Does
    // nothing (returns false) if the mob has died since the caller computed `path`.
    public bool TryStartChase(IReadOnlyList<(ushort X, ushort Y)> path, int orthogonalStepMs, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Alive) return false;
            if (!PathStartsAtCurrentPosition(path)) return false;
            _movement.StartWalk(path, orthogonalStepMs, now);
            _position = new MobPosition(_movement.CurrentX, _movement.CurrentY);
            return true;
        }
    }

    // Defensive invariant: CharacterMovementState.StartWalk trusts its caller completely and will
    // happily "teleport" this instance to path[0] if it differs from the mob's actual current cell
    // (there is no relocation semantic anywhere in this codebase - a walk path only ever describes
    // where an actor already standing at path[0] is going, never where to put it). Enforced here,
    // at MobInstance's own boundary, rather than inside CharacterMovementState, since
    // CharacterMovementState is shared with player movement and has no concept of "this instance's
    // authoritative current position" independent of the path it's given - MobInstance._position is
    // the one thing that IS authoritative here. Must be called under `_gate`.
    private bool PathStartsAtCurrentPosition(IReadOnlyList<(ushort X, ushort Y)> path)
        => path.Count > 0 && path[0].X == _position.X && path[0].Y == _position.Y;

    // Pinned mob_ai_sub_hard's own "target in attack range -> unit_stop_walking" (unit.cpp:2165-
    // 2166) - called by the monster combat domain service's Attack decision so a mob that has just
    // closed to melee range stops advancing further instead of continuing to walk into/past its
    // target's cell.
    public void StopChase()
    {
        lock (_gate) { _movement.Stop(); }
    }

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
            if (killed)
            {
                _state = MobLifecycleState.Dead;
                // Pinned mob_dead's own unlock-on-death (mob.cpp:3863: "md->target_id =
                // md->attacked_id = md->norm_attacked_id = 0") - no stale engagement may survive a
                // death, matching requirement 7's own "mob dies" unlock condition.
                _targetAccountId = null;
                _combatState = MobCombatState.Idle;
                _nextAttackAt = null;
            }
            return (before, after, killed);
        }
    }

    // Idempotent: only the first caller after death schedules a respawn (via
    // the returned bool); a second call while already dead returns false and
    // changes nothing, so a respawn cannot be scheduled twice for one death.
    public bool TryScheduleRespawn(long dueUtcTicks)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Dead || _deadUntilUtcTicks != 0) return false;
            _deadUntilUtcTicks = dueUtcTicks;
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
    public bool TryRespawn(long nowUtcTicks, Func<(bool Success, MobPosition Position)> selectPosition)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Dead || _deadUntilUtcTicks == 0 || nowUtcTicks < _deadUntilUtcTicks) return false;
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
            if (_state != MobLifecycleState.Dead || _deadUntilUtcTicks == 0 || nowUtcTicks < _deadUntilUtcTicks) return false;
            _state = MobLifecycleState.Alive;
            _currentHp = Spawn.Mob.MaxHp;
            _deadUntilUtcTicks = 0;
            _position = position;
            // The one and only place IncarnationId ever advances - exactly once per successful
            // Dead->Alive transition (never on death, scheduling, a not-yet-due attempt, or a
            // failed selectPosition search, all of which return before reaching this line).
            _incarnationId = _incarnationId.Next();
            // Reset movement state entirely on respawn: any in-flight walk from the PREVIOUS life
            // must never continue to mutate a respawned instance's position (an old scheduled
            // movement event must not move a respawned instance to where the dead instance was
            // walking toward). `_nextIdleWalkAt = null` resets to the same "not yet initialized"
            // state a freshly-constructed instance starts in, so idle AI treats a respawned
            // instance exactly like a brand new spawn (matching pinned mob_spawn, which
            // re-initializes next_walktime via the same INVALID_TIMER path for both a genuinely new
            // spawn and a respawn - mob.cpp:1134-1143 calls mob_spawn for both).
            _movement = new CharacterMovementState(Map, position.X, position.Y);
            _nextIdleWalkAt = null;
            // A respawned instance is a brand new mob_spawn per pinned source's own comment above -
            // no engagement from the PREVIOUS life may survive (requirement 7's own "never leave
            // stale account IDs attached to a respawned monster").
            _targetAccountId = null;
            _combatState = MobCombatState.Idle;
            _nextAttackAt = null;
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
    // `!IsMoving`), and `now` has reached `_nextIdleWalkAt`. On the VERY FIRST call for a freshly
    // spawned/respawned instance (`_nextIdleWalkAt == null`, pinned mob.cpp:1681's
    // `next_walktime == INVALID_TIMER` case), this method does NOT authorize a walk yet - it only
    // initializes the deadline to `now + jitter + MIN_RANDOMWALKTIME` and returns false, exactly
    // matching pinned mob_randomwalk's own "initialize next_walktime and return 1 without walking"
    // first-call behavior (mob.cpp:1680-1684; that pinned `return 1` means "the AI tick handled
    // this mob successfully", not "a walk started").
    public bool IsIdleWalkDue(DateTimeOffset now, Func<long> randomJitterMs)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Alive || _movement.IsMoving) return false;

            if (_nextIdleWalkAt is null)
            {
                _nextIdleWalkAt = now.AddMilliseconds(randomJitterMs() + MinRandomWalkTimeMs);
                return false;
            }

            return now >= _nextIdleWalkAt;
        }
    }

    // MIN_RANDOMWALKTIME (mob.hpp:39) - the pinned minimum delay in MILLISECONDS before an idle
    // mob's NEXT random walk consideration, on top of which pinned mob.cpp adds `rnd()%1000`
    // jitter (also milliseconds) both when initializing next_walktime (mob.cpp:1682) and after a
    // walk completes (mob.cpp:1766). Always combined via DateTimeOffset.AddMilliseconds - see
    // _nextIdleWalkAt's own doc comment for why a bare numeric addition to a tick-based field was a
    // real, previously-shipped bug this constant's own unit must never be ambiguous about again.
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
    public void RescheduleAfterFailedIdleWalk(DateTimeOffset now, Func<long> jitterMs)
    {
        lock (_gate)
        {
            _nextIdleWalkAt = now.AddMilliseconds(jitterMs());
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
    public bool TryStartIdleWalk(IReadOnlyList<(ushort X, ushort Y)> path, int orthogonalStepMs, DateTimeOffset now, Func<long> jitterMs)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Alive || _movement.IsMoving) return false;
            if (!PathStartsAtCurrentPosition(path)) return false;
            _movement.StartWalk(path, orthogonalStepMs, now);
            _position = new MobPosition(_movement.CurrentX, _movement.CurrentY);
            // Pinned mob_randomwalk's own post-walk-start rescheduling (mob.cpp:1766):
            // "next_walktime = tick + rnd()%1000 + MIN_RANDOMWALKTIME + unit_get_walkpath_time" -
            // the NEXT idle-walk consideration is scheduled for after this walk's own expected
            // duration completes, not from `now`. Uses CharacterMovementState.TotalWalkPathTimeMs
            // (the EXACT pinned unit_get_walkpath_time sum over each step's own orthogonal/diagonal
            // duration) rather than a uniform orthogonalStepMs*(path.Count-1) approximation, which
            // undercounts any walk containing a diagonal step.
            _nextIdleWalkAt = now.AddMilliseconds(jitterMs() + MinRandomWalkTimeMs + CharacterMovementState.TotalWalkPathTimeMs(path, orthogonalStepMs));
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
    //
    // Correctness note: this method does NOT consume a pending combat retarget by itself - see
    // AdvanceMovementForCombat below, which MonsterEngagementTickProcessor calls instead for any
    // engaged mob. This overload remains exactly as-is for MonsterRuntime's own idle-walk path,
    // which is never engaged (MonsterRuntime.ProcessIdleMovement's own HasActiveTarget guard means
    // idle movement and combat-chase movement are mutually exclusive for a given instance at any
    // one time) and therefore never has a pending retarget to consume in the first place.
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

    // Pinned unit_walktoxy's mid-walk retarget lifecycle (unit.cpp:884-899, applied at the cell
    // boundary by unit_walktoxy_timer's own change_walk_target check, unit.cpp:738-744) - the
    // monster-chase counterpart of MapClientSession.ProcessDueMovementAsync's own identical
    // consume-then-recompute-then-install sequence for player movement (see that method's own
    // "requirement 7" comment for why this must all happen atomically against the cell the
    // instance ACTUALLY just reached, never a stale earlier position).
    //
    // `computeReplacementPath` is a plain delegate (fromX, fromY, toX, toY) -> path, NOT an
    // IMovementPathProvider reference - MobInstance must never depend on that interface directly
    // (matching TryRespawn's own `selectPosition` callback pattern and MonsterRuntime.
    // ProcessIdleMovement's own "path computation is the scheduler's job" split, both cited in this
    // type's own doc comments). The ENTIRE consume -> compute -> install sequence runs under this
    // instance's own lock, so no caller can ever observe the instance between "pending retarget
    // consumed" and "replacement path installed" - the two states MapClientSession's own comment
    // warns must never be allowed to appear separated to an outside reader (a stale warp/target
    // reference could otherwise briefly outlive its walk).
    //
    // Returns the newly crossed cells (same contract as AdvanceMovement), and via `retargetApplied`
    // (true only when a pending retarget existed AND was actually applied this call) tells the
    // caller (MonsterEngagementTickProcessor) whether to report a ChaseInterrupted-shaped "movement
    // changed, tell observers" outcome for this tick, distinct from an ordinary CellCrossed/
    // WalkFinished the idle-walk path already reports via MonsterRuntime.ProcessTick.
    public (IReadOnlyList<(ushort X, ushort Y)> Crossed, bool RetargetApplied) AdvanceMovementForCombat(
        DateTimeOffset now, Func<ushort, ushort, ushort, ushort, IReadOnlyList<(ushort X, ushort Y)>> computeReplacementPath, int orthogonalStepMs)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Alive) return ([], false);
            var crossed = _movement.AdvanceTo(now);
            if (crossed.Count > 0) _position = new MobPosition(_movement.CurrentX, _movement.CurrentY);

            // Pinned unit_walktoxy_timer only ever consults change_walk_target immediately after
            // an actual cell arrival (unit.cpp:738) - a retarget requested mid-step must sit
            // untouched until THIS step's own boundary is reached, never be applied against the
            // in-flight step's still-unchanged current cell. Without this guard, a target that
            // keeps re-requesting the same (or any) destination every 100ms tick while the mob is
            // still mid-step (WalkSpeed's step duration > tick interval) would have this call
            // consume-and-reapply the pending retarget from the CURRENT (unmoved) position on every
            // single tick, via StartWalk below - which resets _stepStartedAt and _pathPosition,
            // permanently pinning the mob at its starting cell no matter how much real time passes.
            if (crossed.Count == 0) return (crossed, false);

            var pendingRetarget = _movement.ConsumePendingRetarget();
            if (pendingRetarget is not { } retarget) return (crossed, false);

            var path = computeReplacementPath(_position.X, _position.Y, retarget.X, retarget.Y);
            if (path.Count < 2) return (crossed, false); // No real path from the reached cell - matches unit_walktobl's own silent-failure contract; the stale destination is simply dropped (pinned change_walk_target is cleared either way by ConsumePendingRetarget above).

            _movement.StartWalk(path, orthogonalStepMs, now);
            _position = new MobPosition(_movement.CurrentX, _movement.CurrentY);
            return (crossed, true);
        }
    }

    // Read-only snapshot of movement state for a scheduler/wire-notification caller that needs to
    // know whether this instance is currently walking and when its next per-cell step is due,
    // without needing its own duplicate CharacterMovementState. Matches
    // CharacterMovementState.NextStepDueAt's own "null means not moving" contract.
    public bool IsWalking { get { lock (_gate) return _movement.IsMoving; } }
    public DateTimeOffset? NextMovementStepDueAt { get { lock (_gate) return _movement.NextStepDueAt; } }
    public (ushort X, ushort Y) MovementDestination { get { lock (_gate) return _movement.Destination; } }

    // The retarget destination awaiting the current in-flight step's boundary, if any - lets a
    // caller (MonsterEngagementTickProcessor.ApplyChaseDecision) recognize "I already asked for
    // this exact destination last tick and it simply hasn't been applied yet" and skip re-issuing
    // an identical TryRetargetChase call every 100ms tick while the mob is still walking toward its
    // OWN current step's boundary. Null means no retarget is currently pending (either none was
    // ever requested, or one was already applied at the last boundary).
    public (ushort X, ushort Y)? PendingChaseDestination { get { lock (_gate) return _movement.PendingRetargetDestination; } }
}
