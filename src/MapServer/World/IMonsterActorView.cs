namespace Athena.Net.MapServer.World;

// Narrow, position/identity-shaped projection of a runtime monster instance - deliberately
// EXCLUDES CurrentHp/NextAttackAt (see MonsterCombatState's own doc comment for where those live
// instead). This is the "actor/simulation-facing" half of the split the Phase 2B plan's own
// "no second monster-position authority in MapServer" design decision calls for: once MapServer
// starts consuming a World-authoritative position/movement projection (a later step - NOT this
// one), every position-dependent read (packet building, range checks, visibility) goes through
// THIS interface so it is mechanically obvious, at every call site, that it reads whichever
// authority actually backs it (today: MobInstance directly; later: a World projection type),
// while CurrentHp/NextAttackAt reads stay visibly routed through the separate MonsterCombatState
// type instead. MobInstance implements this interface unmodified - see that type's own doc
// comments for the full source trace behind each member.
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
