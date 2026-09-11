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

    // Step 7 substep 6 (§14.2): records exact monster lives for which THIS session already completed
    // its OWN authoritative death-vanish projection (CompleteInFlight was called with
    // markProjected: true after a real in-flight entry was retired - see that method's own doc
    // comment). Closes the "late Died" window: releasing the in-flight registration the instant this
    // session's own lethal projection resolves re-opens a period during which the feed's independent
    // Died fan-out - delayed arbitrarily by scheduling jitter, RPC latency, load, or GC, with no upper
    // bound - could otherwise arrive AFTER the registration is gone and hit the ordinary,
    // un-deferred vanish path, producing a duplicate vanish for the very session that already sent
    // its own. Keyed by the EXACT life, never ActorId alone, for the same mixed-incarnation reason
    // _inFlight is. No time-based eviction of any kind lives here - entries are removed only via the
    // lifecycle-triggered ForgetProjectedForActor below (wired to the real Respawned feed entry in
    // substep 7), so a duplicate is suppressed correctly no matter how late it arrives.
    private readonly HashSet<WorldMonsterLifeReference> _alreadyProjected = [];

    // Step A: registers that THIS session has an in-flight local lethal projection for the exact
    // life, called immediately before starting TryMarkMonsterDeadAsync. Idempotent-safe to call again
    // for the same life (overwrites to false/not-yet-observed) - callers only ever call this once per
    // attack attempt in practice (PerformDueRepeatAttackAsync's own single lethal branch).
    public void BeginInFlight(WorldMonsterLifeReference life)
    {
        lock (_gate) { _inFlight[life] = false; }
    }

    // Step C, extended by §14.2's terminal-state fix: called by NotifyMonsterDiedAsync when an
    // authoritative Died feed entry for `life` arrives. Three-way result:
    //   A. `life` has a currently-registered in-flight local lethal projection: record that a Died
    //      was observed while pending (the existing defer behavior) and return true - the caller
    //      must suppress its own immediate wire vanish for THIS session.
    //   B. `life` has NO in-flight registration but IS in `_alreadyProjected` (this session already
    //      sent its own authoritative death vanish for this exact life, and this Died is a late
    //      duplicate arriving after that): return true (suppress) WITHOUT touching `_inFlight` at
    //      all - never recreate an in-flight entry or mutate any deferred flag for a projection that
    //      has already fully resolved.
    //   C. Neither set contains `life`: ordinary case (bystander session, or no lethal projection was
    //      ever attempted here) - return false, caller proceeds with its own normal immediate vanish.
    // Every OTHER observing session is unaffected by any of this and proceeds with its own normal
    // immediate vanish regardless of this result - this arbiter is entirely per-session.
    public bool TryDeferDiedWhileInFlight(WorldMonsterLifeReference life)
    {
        lock (_gate)
        {
            if (_inFlight.ContainsKey(life))
            {
                _inFlight[life] = true; // Record: an authoritative Died was observed while pending.
                return true;
            }
            return _alreadyProjected.Contains(life);
        }
    }

    // Steps D/E/F, extended by §14.2: completes the in-flight registration for `life` and reports
    // whether an authoritative Died was observed while it was pending (via TryDeferDiedWhileInFlight
    // above) - the caller uses this SINGLE atomic read-and-remove to decide, exactly once, whether it
    // still owes this session a deferred authoritative-Died wire cleanup after its own local
    // resolution (MarkedDead/StaleLifeReference/AlreadyDead/transient-failure) is already decided. A
    // life with NO in-flight registration at all (already completed, or never started) reports false
    // and is a silent no-op - never an error, since a caller might legitimately call this defensively.
    //
    // `markProjected`: true when the retiring session itself is about to send (or just sent) the
    // authoritative death vanish directly (its own RPC result, or a replayed result, was the lethal
    // one) - adds `life` to `_alreadyProjected` so a subsequent late Died for this exact life is
    // suppressed via case B above. False for every other retirement reason (a rejection status, or a
    // branch where a DIFFERENT attacker owns the eventual vanish). Critically, `markProjected: true`
    // has NO effect unless a real in-flight entry was actually retired by THIS call - calling this
    // with markProjected:true against a life with no in-flight registration must never manufacture an
    // `_alreadyProjected` marker; the early return below happens before that flag is ever consulted.
    // The return value's meaning never changes: it means ONLY "was Died observed while in flight?",
    // never "already projected" - and calling this performs no callback replay of any kind; a caller
    // that gets `true` back is still responsible for explicitly invoking its own deferred-Died
    // handling (e.g. PerformDeferredAuthoritativeDiedAsync) itself.
    public bool CompleteInFlight(WorldMonsterLifeReference life, bool markProjected)
    {
        lock (_gate)
        {
            if (!_inFlight.Remove(life, out var diedObservedWhilePending)) return false;
            if (markProjected) _alreadyProjected.Add(life);
            return diedObservedWhilePending;
        }
    }

    // Step 7 substep 6 (§14.5), NOT wired anywhere yet (substep 7 connects this to the real
    // Respawned feed entry dispatch): removes every `_alreadyProjected` entry for the same
    // (MapId, SimulationEpoch, ActorId) whose IncarnationId is NOT `exceptIncarnationId` - the
    // structural fix for "an old incarnation's projected-death marker must eventually be freed,
    // without needing to know or guess the specific old IncarnationId value(s)". Preserves
    // `exceptIncarnationId`'s own entry (if any), every other ActorId, and every other MapId/epoch
    // untouched. Correct even if multiple respawns of the same spawn point happened before this
    // session's arbiter caught up (a multi-respawn backlog) - every stale incarnation still on record
    // for that actor is cleared in one call, not just the immediately-prior one. Pure in-memory,
    // synchronous, no I/O - never awaits, cannot throw IOException/OperationCanceledException.
    public void ForgetProjectedForActor(string mapId, WorldSimulationEpoch epoch, uint actorId, WorldMonsterIncarnationId exceptIncarnationId)
    {
        lock (_gate)
        {
            _alreadyProjected.RemoveWhere(life =>
                life.MapId == mapId && life.SimulationEpoch.Equals(epoch) && life.ActorId == actorId &&
                !life.IncarnationId.Value.Equals(exceptIncarnationId.Value));
        }
    }
}
