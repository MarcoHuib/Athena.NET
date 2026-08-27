namespace Athena.Net.MapServer.World;

// What the monster combat domain decided a single engaged MobInstance should do THIS world tick,
// against the target snapshot it was given. The orchestrator (MapTcpServer's monster tick loop)
// applies whichever case is returned - this type never touches MobInstance/session state itself,
// matching the "domain decides, orchestrator applies" split the caller composes this class into.
public abstract record MonsterEngagementDecision
{
    // Pinned mob_ai_sub_hard's target-invalidity branch (mob.cpp:1904-1920) or the mob's own death
    // - the target must be unlocked. Carries no data: MobInstance.TryUnlockTarget needs only `now`/
    // jitter, both supplied by the orchestrator's own TimeProvider/jitter source.
    public sealed record Unlock : MonsterEngagementDecision;
    // Pinned mob_ai_sub_hard's out-of-range branch calling unit_walktobl (mob.cpp:2213) - chase
    // toward the target's cell. `DestinationX/Y` is the target's CURRENT cell at evaluation time
    // (never a stale click-time position - see Evaluate's own doc comment).
    public sealed record Chase(ushort DestinationX, ushort DestinationY) : MonsterEngagementDecision;
    // Pinned mob_ai_sub_hard's in-range branch calling unit_attack (mob.cpp:2153-2166) - the target
    // is within this mob's melee range AND its own attack-delay has elapsed; perform one hit now.
    public sealed record Attack : MonsterEngagementDecision;
    // Target still valid and in range, but this mob's own attack delay has not elapsed yet (pinned
    // ud->attacktimer still pending, unit.cpp:3230) - nothing to do this tick.
    public sealed record Wait : MonsterEngagementDecision;
}

// Source-backed decision rules for ONE engaged MobInstance against a snapshot of its current
// target - the piece explicitly separated out from both MonsterRuntime (idle/movement scheduling
// only, no combat awareness) and MapTcpServer (orchestration only, no rules) per this slice's own
// architectural requirement. Pure: reads MobInstance's engagement/position snapshot and the
// caller-supplied PlayerCombatSnapshot, returns a decision, mutates nothing and sends no packets -
// the orchestrator is responsible for calling back into MobInstance (StartChase/StopChase/
// EnterAttackState/etc.) and MapClientSession (ApplyIncomingMobBasicAttackAsync) based on what is
// returned here.
//
// Traced against pinned mob_ai_sub_hard (mob.cpp:1841-2217), narrowed to exactly the branches an
// already-target-locked, non-looter, non-slave, non-BG-guardian mob like G_PORING can reach:
//   - Target validity (mob.cpp:1904-1920): map mismatch or the target session no longer resolving
//     -> Unlock. This slice does not model pinned's own "chase a few more cells before dropping an
//     out-of-sight target" grace window (mob.cpp:1914-1917, mob_chase_refresh) - see Evaluate's own
//     "deliberately not modeled" note below.
//   - In attack range (battle_check_range, i.e. Chebyshev distance <= effective range - see the
//     walking-target-range-bonus note below) AND the mob's own attack delay has elapsed
//     (mob.cpp:2141-2166 + unit_attack_timer_sub's own attackabletime gate, unit.cpp:3230,3290) ->
//     Attack. In range but delay not yet elapsed -> Wait (matches unit_attack_timer_sub's own
//     "DIFF_TICK(attackabletime,tick)>0 -> re-arm timer, do nothing yet", unit.cpp:2971-2972/3337).
//   - Out of range -> Chase toward the target's current cell (mob.cpp:2213's unit_walktobl).
//
// Walking-target +1 range bonus (unit_attack_timer_sub, unit.cpp:3253-3268): "range =
// status_get_range(src); if (unit_is_walking(target) ...) range++;" - this check runs BEFORE the
// sd/md branch split, so it applies identically whichever side is attacking, keyed on whether the
// TARGET (not the attacker) is currently walking. MapClientSession's existing player-attacks-mob
// path already applies this exact rule keyed on the mob's own IsWalking
// (`resolvedRange + (target.IsWalking ? 1 : 0)`); Evaluate below applies the identical pinned rule
// for the reverse direction, keyed on PlayerCombatSnapshot.IsWalking - this is NOT an invented
// "Chebyshev <= AttackRange+1" shortcut, it is the same traced unit_attack_timer_sub condition
// that already justifies the existing player-side bonus, just evaluated for the other party.
public static class MonsterEngagementDomain
{
    // Pinned check_distance_bl's own Chebyshev metric (battle.cpp, used by battle_check_range for
    // any non-PC attacker per that function's own `else` branch - see battle_check_range's doc
    // trace this project's report cites) - NOT the circular check_distance_client_bl used only for
    // BL_PC attackers/skills.
    private static int ChebyshevDistance(int dx, int dy) => Math.Max(Math.Abs(dx), Math.Abs(dy));

    // Deliberately NOT modeled in this slice (disclosed, not silently approximated - matching this
    // project's other basic-attack calculators' own convention): pinned's mob_chase_refresh grace
    // window before dropping a briefly-out-of-map-sync target, db->range3/ChaseRange-based give-up
    // distance (mob.cpp:2208's MSS_ANGRY-only check does not even apply to MSS_RUSH, but a generic
    // "give up chasing if the target got too far away" IS pinned behavior for other states this
    // slice does not reach), and the RUDE_ATTACKED_COUNT skill-retaliation path (mob.cpp:1936-1995)
    // - none of these change the OBSERABLE "does the mob keep wandering off during combat"
    // behavior this task fixes; a target becomes invalid here only via map mismatch or the target
    // session/character no longer resolving (disconnect, teleport, death), which is exactly this
    // task's own item 7 unlock-condition list.
    public static MonsterEngagementDecision Evaluate(MobInstance mob, PlayerCombatSnapshot? target, DateTimeOffset now)
    {
        if (target is not { } snapshot || !snapshot.IsAlive || !string.Equals(snapshot.Map, mob.Map, StringComparison.OrdinalIgnoreCase))
            return new MonsterEngagementDecision.Unlock();

        var position = mob.GetPosition();
        var dx = snapshot.X - position.X;
        var dy = snapshot.Y - position.Y;
        var effectiveRange = mob.Spawn.Mob.AttackRange + (snapshot.IsWalking ? 1 : 0);

        if (ChebyshevDistance(dx, dy) <= effectiveRange)
        {
            var nextAttack = mob.NextAttackAt;
            return nextAttack is null || now >= nextAttack ? new MonsterEngagementDecision.Attack() : new MonsterEngagementDecision.Wait();
        }

        return new MonsterEngagementDecision.Chase(snapshot.X, snapshot.Y);
    }
}
