namespace Athena.Net.MapServer.World;

// What kind of movement-relevant change happened to a MobInstance during one MonsterRuntime.
// ProcessTick (or MonsterEngagementTickProcessor.ProcessAsync) call - distinguishes "the walk just
// started" from "a cell was crossed mid-walk" from "the walk just completed" from "a combat
// interruption stopped the walk", because pinned clif_move/clif_fixpos are NOT sent for all of
// these the same way:
//   - unit_walktoxy_nextcell (unit.cpp:180-247) only passes sendMove=true from unit_walktoxy's own
//     initial call (unit.cpp:317, the WalkStarted case) - the per-cell continuation call from
//     unit_walktoxy_timer (unit.cpp:749) always passes sendMove=false, so an ordinary CellCrossed
//     event must NOT re-send the walk-entry packet.
//   - An ordinary walk reaching its natural destination (WalkFinished) sends nothing at all
//     (unit.cpp:186-192, no clif_fixpos).
//   - A COMBAT interruption of an in-flight walk (mob_ai_sub_hard's own "target in range ->
//     unit_stop_walking(md, USW_FIXPOS|USW_RELEASE_TARGET)", unit.cpp:2165-2166) is different:
//     USW_FIXPOS makes pinned unit_stop_walking call clif_fixpos (unit.cpp:1732-1737) - this is the
//     ChaseInterrupted case, and IS the one case where the capture-verified 0x0088 ZC_STOPMOVE
//     packet (IroMonsterActorPackets.BuildStopMove) is sent, at the mob's authoritative current
//     cell. See MapClientSession.NotifyMonsterMovedAsync for how each Kind maps to (or deliberately
//     withholds) a wire packet.
public enum MonsterMovementChangeKind { WalkStarted, CellCrossed, WalkFinished, ChaseInterrupted }

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
//     CURRENT position, not spawn position (mob.cpp:1675,1698-1699,1701-1751).
//   - The candidate ITERATION ORDER is ported exactly, not merely the search space - see
//     TryFindIdleWalkPath's own doc comment for the full derivation from `r`/`rdir` (mob.cpp:
//     1696-1751, "Randomize direction in which we iterate to prevent monster cluttering up in one
//     corner").
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
public sealed class MonsterRuntime(MonsterRegistry monsters, IMapCollisionProvider collisionProvider, IMovementPathProvider pathProvider, TimeProvider timeProvider, Func<int>? randomSearchSeed = null, Func<int>? randomDirection = null, Func<long>? randomJitterMs = null)
{
    private const int SearchRadius = 7; // Pinned mob_randomwalk's `d=7` (mob.cpp:1675) - a 15x15 square.
    private const int SearchSpan = SearchRadius * 2 + 1; // 15 - pinned `d*2+1`.

    // Pinned `r=rnd()` (mob.cpp:1696) - a single unbounded random draw whose OWN value is reused for
    // both the dx and dy starting offsets (mob.cpp:1698-1699: dx=r%(d*2+1)-d, dy=r/(d*2+1)%(d*2+1)-d)
    // - NOT two independent random draws. Injected so tests can pin the exact starting candidate.
    private readonly Func<int> _randomSearchSeed = randomSearchSeed ?? DefaultRandomSearchSeed;
    // Pinned `rdir=rnd()%4` (mob.cpp:1697) - selects which of the 4 pinned iteration directions
    // this search uses (mob.cpp:1701-1751's switch) - see TryFindIdleWalkPath's own doc comment.
    private readonly Func<int> _randomDirection = randomDirection ?? DefaultRandomDirection;
    // Pinned `rnd()%1000` (mob.cpp:1682,1766, mob.cpp:2065) - injected (not called inline via
    // Random.Shared) so both the idle-walk-due initialization jitter, the post-success
    // reschedule jitter, and the post-failure reschedule jitter can be driven deterministically by
    // tests, matching this project's existing TimeProvider-based determinism philosophy.
    private readonly Func<long> _randomJitterMs = randomJitterMs ?? DefaultRandomJitterMs;

    private static int DefaultRandomSearchSeed() => System.Random.Shared.Next(0, int.MaxValue);
    private static int DefaultRandomDirection() => System.Random.Shared.Next(0, 4);
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
        var changed = new List<MonsterMovementChange>();

        foreach (var instance in monsters.AllInstances)
        {
            if (!instance.IsAlive) continue;

            if (ProcessIdleMovement(instance, now))
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
    private bool ProcessIdleMovement(MobInstance instance, DateTimeOffset now)
    {
        // MD_CANMOVE required, MD_NORANDOMWALK forbids it even so (mob.cpp:1687,1689) - checked
        // here (mode is Spawn.Mob data) before ever asking MobInstance whether timing is due, so a
        // stationary mob's next-idle-walk deadline is never even initialized.
        var mode = instance.Spawn.Mob.Mode;
        if (!mode.HasFlag(MobMode.CanMove) || mode.HasFlag(MobMode.NoRandomWalk)) return false;

        // Pinned mob_ai_sub_hard only ever reaches its mob_randomwalk call inside the "if (!tbl)"
        // branch (mob.cpp:2043-2069) - a mob with a valid combat target never falls into that
        // branch at all, so mob_randomwalk is never even considered while engaged. Checked here
        // (not inside MobInstance.IsIdleWalkDue) so a still-engaged mob's next-idle-walk deadline is
        // never advanced/consulted while combat is deciding its movement instead - see
        // MonsterEngagementDomain's own doc comment for where that decision actually happens.
        if (instance.HasActiveTarget) return false;

        if (!instance.IsIdleWalkDue(now, _randomJitterMs)) return false;

        if (!collisionProvider.TryGetMap(instance.Map, out var map))
        {
            instance.RescheduleAfterFailedIdleWalk(now, _randomJitterMs);
            return false;
        }

        var position = instance.GetPosition();
        var walkSpeed = instance.Spawn.Mob.WalkSpeed;
        if (!TryFindIdleWalkPath(map, position.X, position.Y, walkSpeed, out var path))
        {
            // Pinned mob_ai_sub_hard's post-failure reschedule (mob.cpp:2058-2066) - see
            // MobInstance.RescheduleAfterFailedIdleWalk's own doc comment.
            instance.RescheduleAfterFailedIdleWalk(now, _randomJitterMs);
            return false;
        }

        return instance.TryStartIdleWalk(path, walkSpeed, now, _randomJitterMs);
    }

    // Pinned mob_randomwalk's candidate search (mob.cpp:1696-1751), ported exactly - not just the
    // search SPACE but the actual ITERATION ORDER, per the pinned source's own comment: "Randomize
    // direction in which we iterate to prevent monster cluttering up in one corner". Combined
    // "CELL_CHKPASS && unit_walktoxy" success condition (mob.cpp:1704) is reproduced as ONE loop
    // that does not stop until BOTH a traversal-valid candidate cell AND a real computable path to
    // it are found - an individually-walkable-but-unreachable candidate must not end the search.
    //
    // Exact derivation (mob.cpp:1696-1751):
    //   r = rnd(); rdir = rnd()%4;
    //   dx = r % (d*2+1) - d; dy = r / (d*2+1) % (d*2+1) - d;   // ONE shared random value r.
    //   max = (d*2+1)*(d*2+1);                                  // 225 candidates total.
    //   for (i = 0; i < max; i++) {
    //       candidate = (dx,dy) offset from the mob's CURRENT position;
    //       if (candidate != own cell && CELL_CHKPASS(candidate) && unit_walktoxy(candidate)) break;
    //       // advance (dx,dy) by +-d in one axis, wrapping (with carry into the other axis) per rdir
    //   }
    // The four rdir cases step one axis by +-d and, on overflow past +-d, wrap that axis back
    // around (mod d*2+1) AND carry one +-d step into the OTHER axis - this is what makes each rdir
    // value visit all 225 distinct (dx,dy) offsets exactly once (verified independently: for d=7,
    // gcd(d, d*2+1)=gcd(7,15)=1, so a fixed +-7 stride mod 15 is a full-period permutation of the
    // 15x15 grid) before ever repeating, matching pinned source's own guarantee that every distinct
    // 15x15 candidate is tried exactly once per call regardless of which rdir was rolled.
    private bool TryFindIdleWalkPath(MapCollisionMap map, ushort currentX, ushort currentY, int walkSpeed, out IReadOnlyList<(ushort X, ushort Y)> path)
    {
        var r = _randomSearchSeed();
        var rdir = ((_randomDirection() % 4) + 4) % 4; // Defensive modulo: tolerate an injected value outside [0,4) without going out of switch range.
        var dx = Mod(r, SearchSpan) - SearchRadius;
        var dy = Mod(r / SearchSpan, SearchSpan) - SearchRadius;

        for (var i = 0; i < SearchSpan * SearchSpan; i++)
        {
            if (TryCandidatePath(map, currentX, currentY, dx, dy, walkSpeed, out path)) return true;

            switch (rdir)
            {
                case 0:
                    dx += SearchRadius;
                    if (dx > SearchRadius)
                    {
                        dx -= SearchSpan;
                        dy += SearchRadius;
                        if (dy > SearchRadius) dy -= SearchSpan;
                    }
                    break;
                case 1:
                    dx -= SearchRadius;
                    if (dx < -SearchRadius)
                    {
                        dx += SearchSpan;
                        dy -= SearchRadius;
                        if (dy < -SearchRadius) dy += SearchSpan;
                    }
                    break;
                case 2:
                    dy += SearchRadius;
                    if (dy > SearchRadius)
                    {
                        dy -= SearchSpan;
                        dx += SearchRadius;
                        if (dx > SearchRadius) dx -= SearchSpan;
                    }
                    break;
                case 3:
                    dy -= SearchRadius;
                    if (dy < -SearchRadius)
                    {
                        dy += SearchSpan;
                        dx -= SearchRadius;
                        if (dx < -SearchRadius) dx += SearchSpan;
                    }
                    break;
            }
        }

        path = [];
        return false;
    }

    // C#'s % is remainder (can be negative for a negative dividend), while pinned C's rnd() is
    // always non-negative so mob.cpp's own `%` never needs this - kept only as a defensive
    // normalization in case an injected randomSearchSeed test double supplies a negative value.
    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;

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
