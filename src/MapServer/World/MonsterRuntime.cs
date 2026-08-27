namespace Athena.Net.MapServer.World;

// World-owned mob AI/movement scheduler - the "one scheduler/tick loop" this project's runtime
// architecture requires instead of one Timer/Task per monster (matching MonsterRegistry.
// ProcessDueRespawns' own existing "no timer per entry" philosophy, and this project's broader
// CharacterStatusEffectState precedent). Owns no monster state itself (MonsterRegistry/MobInstance
// remain the authoritative owners) - this type only decides WHEN each instance's idle-walk AI is
// due and WHEN its in-progress movement should advance, using an injected TimeProvider so behavior
// is deterministic/testable rather than depending on real wall-clock timers.
//
// Source-traced against pinned mob_randomwalk (mob.cpp:1673-1767) for the idle-walk decision:
//   - Only a mob with MD_CANMOVE and without MD_NORANDOMWALK is ever considered
//     (mob.cpp:1687-1689) - MobInstance.IsIdleWalkDue itself also re-checks Alive/not-already-
//     walking, but the MODE check specifically happens here because it is Spawn.Mob.Mode data,
//     which MobInstance's own methods deliberately do not special-case (they operate generically
//     on whatever mode a mob happens to have).
//   - The search radius is a 15x15 square (d=7, i.e. -7..+7 on each axis) centered on the mob's
//     CURRENT position, not spawn position (mob.cpp:1675,1698-1699,1701-1751) - reproduced here as
//     a simple uniform scan (this project's injected `randomJitterMs`/candidate order does not
//     reproduce pinned mob_randomwalk's own specific rdir-based spiral-search traversal order bit-
//     for-bit; it reproduces the same SEARCH SPACE and SUCCESS CONDITION - "first legal
//     CELL_CHKPASS cell with a real computable path" - which is the behaviorally-relevant part for
//     this slice, not the exact scan order pinned source uses to find it).
//   - The candidate cell predicate is CELL_CHKPASS (mob.cpp:1704) - reproduced as
//     MapCollisionMap.IsTraversalCell, matching this project's existing centralization of that
//     pinned semantic (see IsTraversalCell's own doc comment).
//   - A found candidate still needs `unit_walktoxy` to succeed, i.e. a real computable path
//     (mob.cpp:1704) - reproduced by actually invoking IMovementPathProvider.ComputePath, not just
//     accepting any walkable candidate cell blindly.
//   - No legal candidate this tick -> the mob simply stays idle and is reconsidered on its next
//     due tick (mob.cpp:1752-1763's `i==max` branch) - reproduced by IsIdleWalkDue's own
//     "return false, no walk started" path when no candidate/path is found here either; this class
//     does NOT force a reschedule immediately, matching the low move_fail_count case pinned source
//     also just tries again at the next _nextIdleWalkTimestamp.
//
// Per-cell movement advancement (unit_walktoxy_timer/unit_walktoxy_nextcell, unit.cpp:180-247,542)
// is reproduced generically: every alive instance's MobInstance.AdvanceMovement(now) is called
// once per Tick, letting CharacterMovementState's own per-cell timing (shared with player
// movement) decide how many cells (zero or more) actually crossed since the last tick - there is
// no per-mob timer; ProcessTick is meant to be invoked periodically by ONE caller-owned loop
// (MapServerApp's live path) at a cadence far shorter than WalkSpeed so movement still looks
// smooth, not once per pinned-exact-cell-duration.
public sealed class MonsterRuntime(MonsterRegistry monsters, IMapCollisionProvider collisionProvider, IMovementPathProvider pathProvider, TimeProvider timeProvider, Func<int, int, int>? randomInclusiveRange = null)
{
    private const int SearchRadius = 7; // Pinned mob_randomwalk's `d=7` (mob.cpp:1675) - a 15x15 square.
    private readonly Func<int, int, int> _randomInclusiveRange = randomInclusiveRange ?? DefaultRandomInclusiveRange;

    private static int DefaultRandomInclusiveRange(int minInclusive, int maxInclusive) =>
        System.Random.Shared.Next(minInclusive, maxInclusive + 1);

    // One tick: for every alive instance, consider starting a new idle walk (if none is already in
    // progress and its mode/timing allow it) and advance any in-progress walk. Returns the set of
    // instances whose position actually changed this tick (newly started walks AND instances that
    // crossed at least one cell) so a caller (MapClientSession's periodic loop) knows which
    // instances need a fresh client-facing movement/position notification - never every instance
    // unconditionally.
    public IReadOnlyList<MobInstance> ProcessTick()
    {
        var now = timeProvider.GetUtcNow();
        var nowTicks = now.UtcTicks;
        var changed = new List<MobInstance>();

        foreach (var instance in monsters.AllInstances)
        {
            if (!instance.IsAlive) continue;

            if (ProcessIdleMovement(instance, nowTicks, now))
            {
                changed.Add(instance);
                continue; // A just-started walk already reflects its first cell; no need to also AdvanceMovement this same tick.
            }

            var crossed = instance.AdvanceMovement(now);
            if (crossed.Count > 0) changed.Add(instance);
        }

        return changed;
    }

    // Returns true if a new idle walk was started this call.
    private bool ProcessIdleMovement(MobInstance instance, long nowTicks, DateTimeOffset now)
    {
        // MD_CANMOVE required, MD_NORANDOMWALK forbids it even so (mob.cpp:1687,1689) - checked
        // here (mode is Spawn.Mob data) before ever asking MobInstance whether timing is due, so a
        // stationary mob's _nextIdleWalkTimestamp is never even initialized.
        var mode = instance.Spawn.Mob.Mode;
        if (!mode.HasFlag(MobMode.CanMove) || mode.HasFlag(MobMode.NoRandomWalk)) return false;

        if (!instance.IsIdleWalkDue(nowTicks, () => Random.Shared.Next(0, 1000))) return false;

        if (!collisionProvider.TryGetMap(instance.Map, out var map)) return false;

        var position = instance.GetPosition();
        if (!TryFindIdleWalkDestination(map, position.X, position.Y, _randomInclusiveRange, out var destX, out var destY)) return false;

        var path = pathProvider.ComputePath(instance.Map, position.X, position.Y, destX, destY);
        if (path.Count < 2) return false; // No real path (or already there) - matches pinned unit_walktoxy failing silently.

        var walkSpeed = instance.Spawn.Mob.WalkSpeed;
        return instance.TryStartIdleWalk(path, walkSpeed, nowTicks, now);
    }

    // Pinned mob_randomwalk's candidate search (mob.cpp:1696-1751): picks a RANDOM (dx,dy) offset
    // within the 15x15 square around the mob's current position first (mob.cpp:1696-1699), and
    // only falls back to scanning the remaining candidates in the square if that first random pick
    // isn't a legal CELL_CHKPASS cell (mob.cpp:1701-1751's spiral continuation). Reproduced here as
    // "pick one random candidate first, then fall back to a full deterministic scan of the
    // remaining candidates" - the same overall search space and the same "randomized, not always
    // the same offset" behavioral property, without reproducing pinned source's specific
    // rdir-seeded spiral traversal order bit-for-bit (see this class's own doc comment for why that
    // distinction doesn't matter for this slice: the observable requirement is a legal,
    // non-deterministic destination choice, not an exact replica of the internal scan order).
    private bool TryFindIdleWalkDestination(MapCollisionMap map, ushort currentX, ushort currentY, Func<int, int, int> randomInclusiveRange, out ushort destX, out ushort destY)
    {
        var randomDx = randomInclusiveRange(-SearchRadius, SearchRadius);
        var randomDy = randomInclusiveRange(-SearchRadius, SearchRadius);
        if (TryCandidate(map, currentX, currentY, randomDx, randomDy, out destX, out destY)) return true;

        for (var dy = -SearchRadius; dy <= SearchRadius; dy++)
        {
            for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
            {
                if (dx == randomDx && dy == randomDy) continue; // Already tried above.
                if (TryCandidate(map, currentX, currentY, dx, dy, out destX, out destY)) return true;
            }
        }

        destX = 0;
        destY = 0;
        return false;
    }

    private static bool TryCandidate(MapCollisionMap map, ushort currentX, ushort currentY, int dx, int dy, out ushort destX, out ushort destY)
    {
        destX = 0;
        destY = 0;
        if (dx == 0 && dy == 0) return false;
        var x = currentX + dx;
        var y = currentY + dy;
        if (x < 0 || y < 0 || x > ushort.MaxValue || y > ushort.MaxValue) return false;
        if (!map.IsInBounds((ushort)x, (ushort)y) || !map.IsTraversalCell((ushort)x, (ushort)y)) return false;

        destX = (ushort)x;
        destY = (ushort)y;
        return true;
    }
}
