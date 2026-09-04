namespace Athena.Net.MapServer.World;

// The truly-shared portion of MonsterEngagementDomain.Evaluate (MapServer) and
// WorldMonsterEngagementRules.Evaluate (World, Athena.Net.World namespace) - target
// map/alive validity, pinned check_distance_bl's Chebyshev metric, and the walking-target +1
// range bonus (unit_attack_timer_sub, unit.cpp:3253-3268 - see MonsterEngagementDomain's own
// doc comment for the full pinned trace). Deliberately excludes attack cadence
// (NextAttackAt/Attack/Wait): that stays MapServer-local, layered on top of this result by
// MonsterEngagementDomain itself, never duplicated here or moved into World.
//
// Takes plain primitives rather than either caller's own snapshot type (PlayerCombatSnapshot in
// MapServer, WorldPlayerPresence in World.Contracts) so this file - file-linked into
// Athena.World.Monsters exactly like MobInstance.cs/MonsterRuntime.cs already are - stays free of
// a dependency on either side's snapshot shape. Both callers already have a target snapshot in
// hand and just unpack it into these parameters; only World's caller (WorldMonsterEngagementRules)
// resolves the target as InAttackRange vs Chase, and only MapServer's caller
// (MonsterEngagementDomain) further gates InAttackRange behind NextAttackAt.
public static class MonsterTargetRangeRules
{
    public static int ChebyshevDistance(int dx, int dy) => Math.Max(Math.Abs(dx), Math.Abs(dy));

    // True if the target is valid (alive, on the same map as the mob) and within the mob's
    // effective attack range (AttackRange, +1 if the target is currently walking). False for
    // either an invalid target (caller must Unlock) or a valid-but-out-of-range target (caller
    // must Chase toward targetX/targetY).
    public static bool IsTargetInRange(
        string mobMap, ushort mobX, ushort mobY, int attackRange,
        string? targetMap, bool targetIsAlive, ushort targetX, ushort targetY, bool targetIsWalking)
    {
        if (!targetIsAlive || targetMap is null || !string.Equals(targetMap, mobMap, StringComparison.OrdinalIgnoreCase))
            return false;

        var dx = targetX - mobX;
        var dy = targetY - mobY;
        var effectiveRange = attackRange + (targetIsWalking ? 1 : 0);
        return ChebyshevDistance(dx, dy) <= effectiveRange;
    }

    // True if the target is valid at all (alive, on the same map) - distinguishes "must Unlock"
    // from "valid but out of range, must Chase" for callers that need that distinction directly
    // (both existing Evaluate implementations do, since Chase only applies to a still-valid target).
    public static bool IsTargetValid(string mobMap, string? targetMap, bool targetIsAlive) =>
        targetIsAlive && targetMap is not null && string.Equals(targetMap, mobMap, StringComparison.OrdinalIgnoreCase);
}
