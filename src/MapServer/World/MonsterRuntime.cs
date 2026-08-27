namespace Athena.Net.MapServer.World;

// What kind of movement-relevant change happened to a MobInstance during one MonsterRuntime.
// ProcessTick call - distinguishes "the walk just started" from "a cell was crossed mid-walk" from
// "the walk just completed", because pinned clif_move is NOT sent for all three the same way:
// unit_walktoxy_nextcell (unit.cpp:180-247) only passes sendMove=true from unit_walktoxy's own
// initial call (unit.cpp:317, the WalkStarted case) - the per-cell continuation call from
// unit_walktoxy_timer (unit.cpp:749) always passes sendMove=false, so an ordinary CellCrossed event
// must NOT re-send the walk-entry packet. See MapClientSession.NotifyMonsterMovedAsync for how each
// Kind maps to (or deliberately withholds) a wire packet.
public enum MonsterMovementChangeKind { WalkStarted, CellCrossed, WalkFinished }

public readonly record struct MonsterMovementChange(MobInstance Instance, MonsterMovementChangeKind Kind);

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
//     for-bit; it reproduces the same SEARCH SPACE and SUCCESS CONDITION - see TryFindIdleWalkPath's
//     own doc comment - which is the behaviorally-relevant part for this slice, not the exact scan
//     order pinned source uses to find it).
//   - The candidate cell predicate is CELL_CHKPASS (mob.cpp:1704) - reproduced as
//     MapCollisionMap.IsTraversalCell, matching this project's existing centralization of that
//     pinned semantic (see IsTraversalCell's own doc comment).
//   - Critically, pinned mob.cpp:1704 evaluates `map_getcell(...CELL_CHKPASS) && unit_walktoxy(...)`
//     as ONE combined condition INSIDE the candidate loop - an individually walkable but
//     UNREACHABLE candidate (CELL_CHKPASS true, unit_walktoxy false) does NOT stop the search; the
//     loop simply continues to the next candidate. TryFindIdleWalkPath below reproduces this
//     exactly: it does not stop at the first traversal-valid cell, only at the first candidate that
//     is BOTH traversal-valid AND has a real computable path.
//   - No legal candidate this tick -> the mob simply stays idle, but pinned mob_ai_sub_hard (the
//     actual CALLER of mob_randomwalk, mob.cpp:2058-2066) reschedules next_walktime on this failure
//     rather than leaving it untouched - see MobInstance.RescheduleAfterFailedIdleWalk.
//
// Per-cell movement advancement (unit_walktoxy_timer/unit_walktoxy_nextcell, unit.cpp:180-247,542)
// is reproduced generically: every alive instance's MobInstance.AdvanceMovement(now) is called
// once per Tick, letting CharacterMovementState's own per-cell timing (shared with player
// movement) decide how many cells (zero or more) actually crossed since the last tick - there is
// no per-mob timer; ProcessTick is meant to be invoked periodically by ONE caller-owned loop
// (MapServerApp's live path) at a cadence far shorter than WalkSpeed so movement still looks
// smooth, not once per pinned-exact-cell-duration.
public sealed class MonsterRuntime(MonsterRegistry monsters, IMapCollisionProvider collisionProvider, IMovementPathProvider pathProvider, TimeProvider timeProvider, Func<int, int, int>? randomInclusiveRange = null, Func<long>? randomJitterMs = null)
{
    private const int SearchRadius = 7; // Pinned mob_randomwalk's `d=7` (mob.cpp:1675) - a 15x15 square.
    private readonly Func<int, int, int> _randomInclusiveRange = randomInclusiveRange ?? DefaultRandomInclusiveRange;
    // Pinned `rnd()%1000` (mob.cpp:1682,1766, mob.cpp:2065) - injected (not called inline via
    // Random.Shared) so both the idle-walk-due initialization jitter, the post-success
    // reschedule jitter, and the post-failure reschedule jitter can be driven deterministically by
    // tests, matching this project's existing TimeProvider-based determinism philosophy.
    private readonly Func<long> _randomJitterMs = randomJitterMs ?? DefaultRandomJitterMs;

    private static int DefaultRandomInclusiveRange(int minInclusive, int maxInclusive) =>
        System.Random.Shared.Next(minInclusive, maxInclusive + 1);

    private static long DefaultRandomJitterMs() => System.Random.Shared.Next(0, 1000);

    // One tick: for every alive instance, consider starting a new idle walk (if none is already in
    // progress and its mode/timing allow it) and advance any in-progress walk. Returns one
    // MonsterMovementChange per instance whose position/walk-state actually changed this tick,
    // tagged with WHAT changed - see MonsterMovementChangeKind's own doc comment for why a caller
    // (MapClientSession.NotifyMonsterMovedAsync) must not treat every change the same way: only
    // WalkStarted (and, per that method's own doc comment, WalkFinished for a stop notification)
    // maps to a wire packet under ordinary pinned clif_move semantics - an ordinary CellCrossed must
    // NOT resend the walk-entry packet.
    public IReadOnlyList<MonsterMovementChange> ProcessTick()
    {
        var now = timeProvider.GetUtcNow();
        var nowTicks = now.UtcTicks;
        var changed = new List<MonsterMovementChange>();

        foreach (var instance in monsters.AllInstances)
        {
            if (!instance.IsAlive) continue;

            if (ProcessIdleMovement(instance, nowTicks, now))
            {
                changed.Add(new MonsterMovementChange(instance, MonsterMovementChangeKind.WalkStarted));
                continue; // A just-started walk already reflects its first cell; no need to also AdvanceMovement this same tick.
            }

            var wasWalking = instance.IsWalking;
            var crossed = instance.AdvanceMovement(now);
            if (crossed.Count == 0) continue;

            // wasWalking is always true here (AdvanceMovement only ever crosses cells for an
            // in-progress walk), so this distinguishes "still walking after this tick's crossings"
            // (CellCrossed - pinned unit_walktoxy_timer's ordinary sendMove=false continuation) from
            // "the walk's last cell was just crossed, ending it this same tick" (WalkFinished).
            var kind = instance.IsWalking ? MonsterMovementChangeKind.CellCrossed : MonsterMovementChangeKind.WalkFinished;
            changed.Add(new MonsterMovementChange(instance, kind));
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

        if (!instance.IsIdleWalkDue(nowTicks, _randomJitterMs)) return false;

        if (!collisionProvider.TryGetMap(instance.Map, out var map))
        {
            instance.RescheduleAfterFailedIdleWalk(nowTicks, _randomJitterMs);
            return false;
        }

        var position = instance.GetPosition();
        var walkSpeed = instance.Spawn.Mob.WalkSpeed;
        if (!TryFindIdleWalkPath(map, position.X, position.Y, walkSpeed, out var path))
        {
            // Pinned mob_ai_sub_hard's post-failure reschedule (mob.cpp:2058-2066) - see
            // MobInstance.RescheduleAfterFailedIdleWalk's own doc comment.
            instance.RescheduleAfterFailedIdleWalk(nowTicks, _randomJitterMs);
            return false;
        }

        return instance.TryStartIdleWalk(path, walkSpeed, nowTicks, now, _randomJitterMs);
    }

    // Pinned mob_randomwalk's candidate search (mob.cpp:1696-1751), with the combined
    // "CELL_CHKPASS && unit_walktoxy" success condition (mob.cpp:1704) reproduced as ONE loop that
    // does not stop until BOTH a traversal-valid candidate cell AND a real computable path to it
    // are found - an individually-walkable-but-unreachable candidate must not end the search (see
    // this class's own doc comment for the exact pinned-line citation). Picks a RANDOM (dx,dy)
    // offset within the 15x15 square around the mob's CURRENT position first (mob.cpp:1696-1699),
    // and only falls back to scanning the remaining candidates in the square if that first random
    // pick doesn't satisfy the combined condition (mob.cpp:1701-1751's spiral continuation) -
    // reproducing the same overall search space/order-independence and the same "randomized, not
    // always the same offset" behavioral property, without reproducing pinned source's specific
    // rdir-seeded spiral traversal order bit-for-bit (see this class's own doc comment for why that
    // distinction doesn't matter for this slice).
    private bool TryFindIdleWalkPath(MapCollisionMap map, ushort currentX, ushort currentY, int walkSpeed, out IReadOnlyList<(ushort X, ushort Y)> path)
    {
        var randomDx = _randomInclusiveRange(-SearchRadius, SearchRadius);
        var randomDy = _randomInclusiveRange(-SearchRadius, SearchRadius);
        if (TryCandidatePath(map, currentX, currentY, randomDx, randomDy, walkSpeed, out path)) return true;

        for (var dy = -SearchRadius; dy <= SearchRadius; dy++)
        {
            for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
            {
                if (dx == randomDx && dy == randomDy) continue; // Already tried above.
                if (TryCandidatePath(map, currentX, currentY, dx, dy, walkSpeed, out path)) return true;
            }
        }

        path = [];
        return false;
    }

    // A single candidate: valid only when the cell itself is traversal-valid AND
    // IMovementPathProvider actually finds a real route to it (mob.cpp:1704's combined condition -
    // see TryFindIdleWalkPath's own doc comment). Computing the path here (not just checking the
    // cell) is exactly what lets an unreachable-but-walkable candidate fall through to the next
    // candidate instead of ending the search early.
    private bool TryCandidatePath(MapCollisionMap map, ushort currentX, ushort currentY, int dx, int dy, int walkSpeed, out IReadOnlyList<(ushort X, ushort Y)> path)
    {
        path = [];
        if (dx == 0 && dy == 0) return false;
        var x = currentX + dx;
        var y = currentY + dy;
        if (x < 0 || y < 0 || x > ushort.MaxValue || y > ushort.MaxValue) return false;
        if (!map.IsInBounds((ushort)x, (ushort)y) || !map.IsTraversalCell((ushort)x, (ushort)y)) return false;

        var candidatePath = pathProvider.ComputePath(map.MapName, currentX, currentY, (ushort)x, (ushort)y);
        if (candidatePath.Count < 2) return false; // No real path (or already there) - matches pinned unit_walktoxy failing silently.

        path = candidatePath;
        return true;
    }
}
