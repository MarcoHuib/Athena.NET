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
// Correlates exactly ONE attack attempt against exactly ONE operator-selected target ActorId
// (MapClientSession.DebugMoveToAttackTargetActorId - null by default, meaning this type is never
// constructed and every call site touching it is skipped entirely) through the eight events the
// live-acceptance task asked for. Uses TimeProvider.GetTimestamp()/GetElapsedTime (the real
// monotonic high-resolution clock every other timing-sensitive part of this codebase already
// prefers over wall-clock DateTime for elapsed-time measurement) - never a DateTime subtraction,
// which is not guaranteed monotonic and is far coarser-grained on many platforms.
internal sealed class MoveToAttackDiagnostics(TimeProvider timeProvider, uint accountId, uint charId, uint targetActorId)
{
    private long? _correlationStart;

    // True once RecordFirstAttackRequest has started a correlation window that has not yet been
    // closed by RecordFirstDamageWriteCompleted/RecordSecondAttackRequest's own natural conclusion -
    // lets the ONE call site (HandleIroAttackRequestAsync) distinguish "this is the FIRST 0437 for
    // this attempt" (event 1) from "this is a RETRY 0437 following an out-of-range rejection"
    // (event 6) without needing two separately-named call sites of its own.
    public bool HasActiveCorrelation => _correlationStart is not null;

    // Starts a new correlation window for a FIRST (out-of-range-rejected) 0x0437 against the
    // watched target - overwrites any prior in-flight correlation for this session (there is only ever
    // one active repeat-attack target per session, matching RepeatAttackState's own single-slot
    // design, so a fresh first-event start always means a genuinely new attempt).
    public void RecordFirstAttackRequest(ushort playerX, ushort playerY, ushort targetX, ushort targetY)
    {
        _correlationStart = timeProvider.GetTimestamp();
        Log("1_First0437Received", playerX, playerY, targetX, targetY);
    }

    public void RecordAttackFailureWriteCompleted(ushort playerX, ushort playerY, ushort targetX, ushort targetY) =>
        Log("2_0139WriteCompleted", playerX, playerY, targetX, targetY);

    public void RecordAutoWalkMovementRequestReceived(ushort playerX, ushort playerY, ushort requestedX, ushort requestedY) =>
        Log("3_AutoWalk035FReceived", playerX, playerY, requestedX, requestedY);

    public void RecordMovementResponseWriteCompleted(ushort fromX, ushort fromY, ushort toX, ushort toY) =>
        Log("4_0087WriteCompleted", fromX, fromY, toX, toY);

    public void RecordAuthoritativeArrival(ushort playerX, ushort playerY) =>
        Log("5_AuthoritativeArrival", playerX, playerY, null, null);

    public void RecordSecondAttackRequest(ushort playerX, ushort playerY, ushort targetX, ushort targetY) =>
        Log("6_Second0437Received", playerX, playerY, targetX, targetY);

    public void RecordNotifyMonsterAttackedCompleted(ushort playerX, ushort playerY) =>
        Log("7_NotifyMonsterAttackedCompleted", playerX, playerY, null, null);

    public void RecordFirstDamageWriteCompleted(ushort playerX, ushort playerY, ushort targetX, ushort targetY)
    {
        Log("8_First08C8WriteCompleted", playerX, playerY, targetX, targetY);
        // Close this correlation window - a LATER, unrelated fresh attack against the same watched
        // ActorId (e.g. after it respawns, or a second independent approach) must start a new
        // window via RecordFirstAttackRequest, never be misread as "the second 0437 of THIS attempt".
        _correlationStart = null;
    }

    private void Log(string eventName, ushort playerX, ushort playerY, ushort? targetX, ushort? targetY)
    {
        var elapsedMs = _correlationStart is { } start ? timeProvider.GetElapsedTime(start).TotalMilliseconds : 0.0;
        var targetPos = targetX is { } tx && targetY is { } ty ? $" targetPos=({tx},{ty})" : "";
        Logging.MapLogger.Info(
            $"[MOVE-TO-ATTACK-DIAG] event={eventName} elapsedMs={elapsedMs:F3} accountId={accountId} charId={charId} targetActorId={targetActorId} playerPos=({playerX},{playerY}){targetPos}");
    }
}
