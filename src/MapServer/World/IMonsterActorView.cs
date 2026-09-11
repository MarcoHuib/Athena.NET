namespace Athena.Net.MapServer.World;

// Narrow, position/identity-shaped projection of a runtime monster instance - deliberately
// EXCLUDES CurrentHp/NextAttackAt. This is the "actor/simulation-facing" half of the split the
// Phase 2B plan's own "no second monster-position authority in MapServer" design decision calls
// for: every position-dependent read (packet building, range checks, visibility) goes through THIS
// interface so it is mechanically obvious, at every call site, that it reads whichever authority
// actually backs it (MobInstance for the legacy live-attack path; WorldMonsterActorView,
// World-authoritative, everywhere else). MobInstance implements this interface unmodified - see
// that type's own doc comments for the full source trace behind each member.
//
// As of Step 7, CurrentHp/MaxHp are World-authoritative fields carried directly on
// WorldMonsterInstance (see that type's own doc comment) - packet/read-model projection code reads
// them from there directly, never through this interface. NextAttackAt remains a MapServer-local
// cadence concern (MonsterCombatStateStore, staged for rename to MonsterAttackCadenceStore in
// substep 9) and is likewise never exposed here.
//
// IncarnationId is the REAL MonsterIncarnationId MobInstance itself now tracks (see that type's
// own doc comment) - never a stub/placeholder value.
public interface IMonsterActorView
{
    uint ActorId { get; }
    MonsterIncarnationId IncarnationId { get; }
    string Map { get; }
    MobPosition GetPosition();
    int MobId { get; }
    string Name { get; }
    int WalkSpeed { get; }
    bool IsWalking { get; }
    (ushort X, ushort Y) MovementDestination { get; }
}
