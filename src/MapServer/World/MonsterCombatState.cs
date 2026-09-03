namespace Athena.Net.MapServer.World;

// The explicitly MapServer-LOCAL half of a monster's runtime state - CurrentHp/MaxHp/NextAttackAt
// and (later) any other combat-cadence-only data - kept as a SEPARATE type from IMonsterActorView
// (position/identity/movement) so a reviewer can see at a glance, from a method's own parameter
// list, whether it depends on World-simulation-shaped data or on MapServer-local combat data. No
// Orleans/World-contract dependency: this type is pure MapServer domain, exactly like MobInstance
// itself, and stays that way even after a later step keys it by
// (MapId, SimulationEpoch, ActorId, IncarnationId) for the real World-authority cutover - Step 4
// does not need that key yet (no grain wiring here at all), so it is deliberately NOT invented
// prematurely; ActorId/IncarnationId alone are enough to identify which MobInstance this snapshot
// was read from in the meantime.
//
// This is a per-read SNAPSHOT (record, not a live reference) - callers that need a fresh value
// after HP changes must re-read via FromInstance again, matching MobPosition/MobEngagement's own
// "atomic snapshot, re-read when needed" convention on MobInstance itself.
public sealed record MonsterCombatState(uint ActorId, MonsterIncarnationId IncarnationId, uint CurrentHp, uint MaxHp, DateTimeOffset? NextAttackAt)
{
    // The ONE bridge point from MobInstance's own combat fields into this explicit local type -
    // every other consumer takes a MonsterCombatState value, never a MobInstance, so this factory
    // (not a cast, not a callback) is the single visible seam where "the current combat source
    // happens to be MobInstance" lives during this preparatory step. Delegates to
    // MobInstance.CaptureCombatState() rather than reading ActorId/IncarnationId/CurrentHp/
    // NextAttackAt as separate property getters - each of those takes its OWN lock acquisition on
    // MobInstance, so reading them independently could observe a torn mix spanning a concurrent
    // respawn (old IncarnationId with new-life HP, etc.) - see CaptureCombatState's own doc comment.
    public static MonsterCombatState FromInstance(MobInstance instance) => instance.CaptureCombatState();
}
