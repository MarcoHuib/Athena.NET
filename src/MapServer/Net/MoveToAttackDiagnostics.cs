namespace Athena.Net.MapServer.Net;

// TEMPORARY live-acceptance diagnostic (Issue A: "player move-to-attack has a visible pause before
// the first hit"). Answers exactly one question with real, sub-millisecond timing evidence: is the
// gap between "player has authoritatively arrived at the final movement cell" and "the client's own
// second 0x0437 attack request reaches this server" a normal few-hundred-millisecond client cadence,
// or a genuinely large idle gap pointing at a real Athena-side defect. REMOVE this entire file (and
// every call site referencing it) once that question is answered and any resulting production fix
// (if one is needed) has been implemented and verified - this is investigation instrumentation, not
// a permanent feature.
//
// Two arming modes, both opt-in only (this type is never constructed, and every call site touching
// it is skipped entirely, unless one of them is armed):
//   - Exact-ActorId mode (MapClientSession.DebugMoveToAttackTargetActorId): the operator already
//     knows the live ActorId to watch.
//   - Auto-arm mode (MapClientSession.DebugMoveToAttackAutoArm): simpler for live testing - the
//     diagnostic itself picks the target the moment the FIRST out-of-range attack rejection (the
//     genuine start of a move-to-attack sequence) happens for THIS session, requiring no prior
//     knowledge of any ActorId and no restart to arm a specific one.
// Both modes converge on the exact same MoveToAttackDiagnostics instance/event sequence once a
// correlation is established - only HOW the watched targetActorId gets chosen differs.
//
// Correlates exactly ONE attack attempt at a time (one active correlation per session) through the
// eight events the live-acceptance task asked for, using TimeProvider.GetTimestamp()/GetElapsedTime
// (the real monotonic high-resolution clock every other timing-sensitive part of this codebase
// already prefers over wall-clock DateTime for elapsed-time measurement) - never a DateTime
// subtraction, which is not guaranteed monotonic and is far coarser-grained on many platforms.
internal sealed class MoveToAttackDiagnostics(TimeProvider timeProvider, uint accountId, uint charId, bool autoArm)
{
    // Null means "no correlation currently active" - the ONE state this type tracks at a time,
    // matching RepeatAttackState's own single-slot design (a session only ever has one active
    // repeat-attack target). Auto-arm mode sets this the moment the first genuine out-of-range
    // rejection happens for whichever ActorId the player is currently attacking; exact-ActorId mode
    // has it fixed for the type's whole lifetime instead (set once, on construction, via the
    // three-arg overload below - see MapClientSession's own two constructor-site distinction).
    private uint? _watchedTargetActorId;
    private long? _correlationStart;
    private long? _event5Timestamp;
    private long? _event6Timestamp;

    public MoveToAttackDiagnostics(TimeProvider timeProvider, uint accountId, uint charId, uint fixedTargetActorId)
        : this(timeProvider, accountId, charId, autoArm: false) => _watchedTargetActorId = fixedTargetActorId;

    // True once RecordFirstAttackRequest has started a correlation window that has not yet been
    // closed by RecordFirstDamageWriteCompleted's own natural conclusion (or aborted) - lets the ONE
    // call site (HandleIroAttackRequestAsync) distinguish "this is the FIRST 0437 for a fresh
    // attempt" (event 1) from "this is a RETRY 0437 following an out-of-range rejection" (event 6)
    // without needing two separately-named call sites of its own.
    public bool HasActiveCorrelation => _correlationStart is not null;

    // Whether `targetActorId` is the one this instance is currently correlating (exact-ActorId mode:
    // always the fixed watched id; auto-arm mode: whatever the active correlation itself already
    // picked, if any is active at all - auto-arm never "watches" an id before a correlation exists
    // for it).
    public bool IsWatching(uint targetActorId) => _watchedTargetActorId == targetActorId;

    // Auto-arm mode only: called for EVERY out-of-range attack rejection (regardless of target) so
    // this type can pick its own watched target the moment the genuine start of a move-to-attack
    // sequence happens, with no prior operator knowledge of the ActorId required. Returns true (and
    // arms `targetActorId` as the watched target) only the first time this is called with no
    // correlation/watch already active - a no-op (returns false) once a target is already being
    // watched (never re-arms mid-sequence onto a different target) or when not in auto-arm mode (the
    // exact-ActorId constructor already fixed _watchedTargetActorId forever).
    public bool TryAutoArm(uint targetActorId)
    {
        if (!autoArm || _watchedTargetActorId is not null) return false;
        _watchedTargetActorId = targetActorId;
        return true;
    }

    // Starts a new correlation window for a FIRST (out-of-range-rejected) 0x0437 against the watched
    // target - overwrites any prior in-flight correlation for this session (there is only ever one
    // active repeat-attack target per session, so a fresh first-event start always means a genuinely
    // new attempt; an abandoned prior correlation is simply dropped without a final A/B/C report,
    // matching "abort cleanly if the sequence is cancelled").
    public void RecordFirstAttackRequest(ushort playerX, ushort playerY, ushort targetX, ushort targetY)
    {
        _correlationStart = timeProvider.GetTimestamp();
        _event5Timestamp = null;
        _event6Timestamp = null;
        Log("1_First0437Received", playerX, playerY, targetX, targetY);
    }

    public void RecordAttackFailureWriteCompleted(ushort playerX, ushort playerY, ushort targetX, ushort targetY) =>
        Log("2_0139WriteCompleted", playerX, playerY, targetX, targetY);

    public void RecordAutoWalkMovementRequestReceived(ushort playerX, ushort playerY, ushort requestedX, ushort requestedY) =>
        Log("3_AutoWalk035FReceived", playerX, playerY, requestedX, requestedY);

    public void RecordMovementResponseWriteCompleted(ushort fromX, ushort fromY, ushort toX, ushort toY) =>
        Log("4_0087WriteCompleted", fromX, fromY, toX, toY);

    public void RecordAuthoritativeArrival(ushort playerX, ushort playerY)
    {
        _event5Timestamp = timeProvider.GetTimestamp();
        Log("5_AuthoritativeArrival", playerX, playerY, null, null);
    }

    public void RecordSecondAttackRequest(ushort playerX, ushort playerY, ushort targetX, ushort targetY)
    {
        _event6Timestamp = timeProvider.GetTimestamp();
        Log("6_Second0437Received", playerX, playerY, targetX, targetY);
    }

    public void RecordNotifyMonsterAttackedCompleted(ushort playerX, ushort playerY) =>
        Log("7_NotifyMonsterAttackedCompleted", playerX, playerY, null, null);

    public void RecordFirstDamageWriteCompleted(ushort playerX, ushort playerY, ushort targetX, ushort targetY)
    {
        Log("8_First08C8WriteCompleted", playerX, playerY, targetX, targetY);

        // Auto-report the exact durations the live-acceptance task asked for, computed from the
        // real monotonic timestamps captured at events 5/6/8 - never re-derived from the printed
        // (rounded, string-formatted) elapsedMs values above.
        if (_correlationStart is { } start)
        {
            var event8 = timeProvider.GetTimestamp();
            var aMs = _event5Timestamp is { } t5 && _event6Timestamp is { } t6 ? timeProvider.GetElapsedTime(t5, t6).TotalMilliseconds : (double?)null;
            var bMs = _event6Timestamp is { } t6b ? timeProvider.GetElapsedTime(t6b, event8).TotalMilliseconds : (double?)null;
            var cMs = _event5Timestamp is { } t5c ? timeProvider.GetElapsedTime(start, t5c).TotalMilliseconds : (double?)null;
            Logging.MapLogger.Info(
                $"[MOVE-TO-ATTACK-DIAG] SUMMARY accountId={accountId} charId={charId} targetActorId={_watchedTargetActorId} " +
                $"A(arrival->second0437)={Fmt(aMs)}ms B(second0437->first08C8)={Fmt(bMs)}ms C(first0437->arrival)={Fmt(cMs)}ms");
        }

        // Close this correlation window - a LATER, unrelated fresh attack (even against the same
        // watched ActorId, e.g. after it respawns) must start a new window via RecordFirstAttackRequest
        // and, in auto-arm mode, may re-pick a different target entirely - never be misread as "the
        // second 0437 of THIS attempt".
        _correlationStart = null;
        _event5Timestamp = null;
        _event6Timestamp = null;
        if (autoArm) _watchedTargetActorId = null;
    }

    // Aborts the current correlation without a final A/B/C report - used when the sequence is
    // cancelled before completing (e.g. the player moves away/teleports/disconnects mid-sequence,
    // or a fresh attack against a DIFFERENT target replaces the one being watched). No-op if no
    // correlation is currently active.
    public void Abort(string reason)
    {
        if (_correlationStart is null) return;
        Logging.MapLogger.Info($"[MOVE-TO-ATTACK-DIAG] ABORTED accountId={accountId} charId={charId} targetActorId={_watchedTargetActorId} reason={reason}");
        _correlationStart = null;
        _event5Timestamp = null;
        _event6Timestamp = null;
        if (autoArm) _watchedTargetActorId = null;
    }

    private static string Fmt(double? ms) => ms is { } value ? value.ToString("F3") : "n/a";

    private void Log(string eventName, ushort playerX, ushort playerY, ushort? targetX, ushort? targetY)
    {
        var elapsedMs = _correlationStart is { } start ? timeProvider.GetElapsedTime(start).TotalMilliseconds : 0.0;
        var targetPos = targetX is { } tx && targetY is { } ty ? $" targetPos=({tx},{ty})" : "";
        Logging.MapLogger.Info(
            $"[MOVE-TO-ATTACK-DIAG] event={eventName} elapsedMs={elapsedMs:F3} accountId={accountId} charId={charId} targetActorId={_watchedTargetActorId} playerPos=({playerX},{playerY}){targetPos}");
    }
}
