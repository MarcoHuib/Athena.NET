using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// The explicitly MapServer-LOCAL half of a monster's runtime state - CurrentHp/MaxHp/NextAttackAt -
// kept as a SEPARATE type from IMonsterActorView (position/identity/movement) so a reviewer can see
// at a glance, from a method's own parameter list, whether it depends on World-simulation-shaped
// data or on MapServer-local combat data. `IncarnationId` is WorldMonsterIncarnationId (the real
// World wire type) since MonsterCombatStateStore is keyed by the full authoritative
// (MapId, SimulationEpoch, ActorId, IncarnationId) tuple post-cutover (see MonsterCombatKey's own
// doc comment) - this type carries the SAME incarnation representation its own key does, rather
// than MapServer's separate MonsterIncarnationId domain type (used only by legacy/test MobInstance
// composition, which this type also still supports via FromInstance below for exactly that reason).
//
// This is a per-read SNAPSHOT (record, not a live reference) - callers that need a fresh value
// after HP changes must re-read from the store again.
public sealed record MonsterCombatState(uint ActorId, WorldMonsterIncarnationId IncarnationId, uint CurrentHp, uint MaxHp, DateTimeOffset? NextAttackAt)
{
    // Legacy/test bridge point ONLY - production no longer constructs a MonsterCombatState from a
    // live MobInstance at all (there is no local MobInstance for a production monster post-cutover;
    // see MonsterCombatStateStore's own doc comment). Retained for focused unit tests / legacy
    // MonsterRuntime-based test composition that still exercises a local MobInstance directly (see
    // MobInstance.CaptureCombatState's own doc comment for why that method itself is retained too).
    // Delegates to MobInstance.CaptureCombatState() (one atomic locked read) rather than reading
    // ActorId/IncarnationId/CurrentHp/NextAttackAt as separate property getters, converting the
    // MapServer-local MonsterIncarnationId into the WorldMonsterIncarnationId representation this
    // type now carries.
    public static MonsterCombatState FromInstance(MobInstance instance)
    {
        var captured = instance.CaptureCombatState();
        return new MonsterCombatState(captured.ActorId, new WorldMonsterIncarnationId(captured.IncarnationId.Value), captured.CurrentHp, captured.MaxHp, captured.NextAttackAt);
    }
}
