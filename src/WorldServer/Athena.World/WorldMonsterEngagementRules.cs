using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.World;

// World's own copy of MonsterEngagementDomain's target-validity/range decision (see that type's
// own doc comment for the full pinned mob_ai_sub_hard trace this narrows from), extracted so the
// SAME source-backed logic is not duplicated in two places (once here, once in MapServer) - see
// the plan's own "MonsterEngagementDomain split" design decision. Deliberately EXCLUDES
// NextAttackAt/Attack/Wait entirely: World has no reason to know about MapServer's local attack
// cadence (see WorldMonsterEngagementState's own doc comment) - this only ever produces
// Unlock/Chase/InAttackRange.
public abstract record WorldMonsterEngagementDecision
{
    public sealed record Unlock : WorldMonsterEngagementDecision;
    public sealed record Chase(ushort DestinationX, ushort DestinationY) : WorldMonsterEngagementDecision;
    public sealed record InAttackRange : WorldMonsterEngagementDecision;
}

public static class WorldMonsterEngagementRules
{
    // `targetIsWalking` is supplied by the caller (WorldPartitionGrain, which tracks active
    // movements itself via its own _movements dictionary) rather than derived here, so this type
    // has no dependency on the grain's own presence/movement bookkeeping shape. No DateTimeOffset
    // parameter: cadence (NextAttackAt/Attack/Wait) was already excluded from this decision
    // entirely (see this type's own doc comment) and nothing else here is time-dependent, so a
    // `now` parameter would be genuinely unused - removed rather than kept as dead API surface.
    //
    // The map/alive validity + Chebyshev distance + walking-bonus math itself is NOT re-derived
    // here - it is the same MonsterTargetRangeRules (file-linked into Athena.World.Monsters, see
    // that type's own doc comment) MonsterEngagementDomain.Evaluate uses on the MapServer side, so
    // both authorities agree on exactly one source-backed implementation.
    public static WorldMonsterEngagementDecision Evaluate(MobInstance mob, WorldPlayerPresence? target, bool targetIsWalking = false)
    {
        if (target is not { } presence || !MonsterTargetRangeRules.IsTargetValid(mob.Map, presence.MapId, presence.IsAlive))
            return new WorldMonsterEngagementDecision.Unlock();

        var position = mob.GetPosition();
        var inRange = MonsterTargetRangeRules.IsTargetInRange(
            mob.Map, position.X, position.Y, mob.Spawn.Mob.AttackRange,
            presence.MapId, presence.IsAlive, presence.X, presence.Y, targetIsWalking);

        return inRange
            ? new WorldMonsterEngagementDecision.InAttackRange()
            : new WorldMonsterEngagementDecision.Chase(presence.X, presence.Y);
    }
}
