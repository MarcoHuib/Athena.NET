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
    // Pinned check_distance_bl's own Chebyshev metric - identical to
    // MonsterEngagementDomain.ChebyshevDistance's own trace, intentionally not re-derived
    // differently here.
    private static int ChebyshevDistance(int dx, int dy) => Math.Max(Math.Abs(dx), Math.Abs(dy));

    // `targetIsWalking` is supplied by the caller (WorldPartitionGrain, which tracks active
    // movements itself via its own _movements dictionary) rather than derived here, so this type
    // has no dependency on the grain's own presence/movement bookkeeping shape.
    public static WorldMonsterEngagementDecision Evaluate(MobInstance mob, WorldPlayerPresence? target, DateTimeOffset now, bool targetIsWalking = false)
    {
        if (target is not { } presence || !presence.IsAlive || !string.Equals(presence.MapId, mob.Map, StringComparison.OrdinalIgnoreCase))
            return new WorldMonsterEngagementDecision.Unlock();

        var position = mob.GetPosition();
        var dx = presence.X - position.X;
        var dy = presence.Y - position.Y;
        var effectiveRange = mob.Spawn.Mob.AttackRange + (targetIsWalking ? 1 : 0);

        return ChebyshevDistance(dx, dy) <= effectiveRange
            ? new WorldMonsterEngagementDecision.InAttackRange()
            : new WorldMonsterEngagementDecision.Chase(presence.X, presence.Y);
    }
}
