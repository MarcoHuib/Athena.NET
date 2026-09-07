using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Net;

// Step 6 final correctness pass, item 1: closes the race between an attacker's OWN in-flight
// confirmed-lethal-hit projection (PerformDueRepeatAttackAsync's own TryMarkMonsterDeadAsync call)
// and World's independent, authoritative Died feed reaching that SAME session through MapTcpServer's
// separate monster-tick loop (FanOutEntryAsync -> NotifyMonsterDiedAsync) for the SAME exact life.
//
// Before this arbiter existed, MapTcpServer's own doc comment claimed the attacker's local
// confirmed-kill path "necessarily" sends its own death-vanish (and clears its own visibility)
// BEFORE the separate feed loop can ever observe the resulting Died entry - that was only true back
// when the local lethal HP mutation happened BEFORE calling TryMarkMonsterDeadAsync. Once that
// ordering was corrected (World's MarkedDead confirmation now happens FIRST), World's Died feed
// becomes observable to the SAME MapTcpServer tick loop that is ALSO what will eventually poll for
// it, immediately after TryMarkMonsterDeadAsync returns MarkedDead - genuinely concurrently with
// PerformDueRepeatAttackAsync still completing its own CommitConfirmedDeath -> quest/drop -> wire
// projection sequence. Without arbitration, the attacker's own session could receive an authoritative
// Died vanish from the feed loop BEFORE its own local sequence finishes, corrupting packet ordering
// (a bare 0x0080 died with no preceding 0x08C8/0x0977, or two 0x0080 sends).
//
// Keyed by the EXACT life (MapId, SimulationEpoch, ActorId, IncarnationId) - never by ActorId alone,
// since a Died feed entry for a DIFFERENT incarnation of the same ActorId must never be suppressed
// by an in-flight lethal projection for another (already-superseded) incarnation.
//
// Every method here is a short, synchronous, in-memory dictionary operation under one `Lock` -
// mirrors MonsterVisibilityState's own identical convention. No socket write, Orleans RPC, CharServer
// RPC, persistence call, or await may EVER happen while this type's lock is held - callers decide
// what I/O to perform AFTER a method here returns its plain result, never during.
internal sealed class LethalDeathProjectionArbiter
{
    private readonly Lock _gate = new();
    // Value: true once an authoritative Died feed entry for this exact life was observed while the
    // in-flight projection was still pending (the deferred-vanish flag) - absence of a key means "no
    // in-flight local lethal projection for this life is currently registered".
    private readonly Dictionary<WorldMonsterLifeReference, bool> _inFlight = [];

    // Step A: registers that THIS session has an in-flight local lethal projection for the exact
    // life, called immediately before starting TryMarkMonsterDeadAsync. Idempotent-safe to call again
    // for the same life (overwrites to false/not-yet-observed) - callers only ever call this once per
    // attack attempt in practice (PerformDueRepeatAttackAsync's own single lethal branch).
    public void BeginInFlight(WorldMonsterLifeReference life)
    {
        lock (_gate) { _inFlight[life] = false; }
    }

    // Step C: called by NotifyMonsterDiedAsync when an authoritative Died feed entry for `life`
    // arrives. Returns true when a matching in-flight local lethal projection IS currently registered
    // (the caller must defer/suppress its own immediate wire vanish and return without sending
    // anything for the attacker's own session - every OTHER observing session is unaffected and
    // proceeds with its own normal immediate vanish regardless of this result) - false means there is
    // no in-flight projection for this exact life (ordinary case: this session is a bystander, or the
    // local projection already completed/was never started), so the caller proceeds with its own
    // normal immediate vanish.
    public bool TryDeferDiedWhileInFlight(WorldMonsterLifeReference life)
    {
        lock (_gate)
        {
            if (!_inFlight.ContainsKey(life)) return false;
            _inFlight[life] = true; // Record: an authoritative Died was observed while pending.
            return true;
        }
    }

    // Steps D/E/F: completes the in-flight registration for `life` and reports whether an
    // authoritative Died was observed while it was pending (via TryDeferDiedWhileInFlight above) -
    // the caller uses this SINGLE atomic read-and-remove to decide, exactly once, whether it still
    // owes this session a deferred authoritative-Died wire cleanup after its own local resolution
    // (MarkedDead/StaleLifeReference/AlreadyDead/transient-failure) is already decided. A life with NO
    // in-flight registration at all (already completed, or never started) reports false and is a
    // silent no-op - never an error, since a caller might legitimately call this defensively.
    public bool CompleteInFlight(WorldMonsterLifeReference life)
    {
        lock (_gate)
        {
            if (!_inFlight.Remove(life, out var diedObservedWhilePending)) return false;
            return diedObservedWhilePending;
        }
    }
}
