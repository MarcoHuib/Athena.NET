using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Net;

// Coordinates LOCAL wire-projection ordering for one monster life this MapServer process is
// currently killing via a player attack, against this SAME process's own independent World-feed
// Died observation (MapTcpServer's monster-tick loop). Never authoritative state and never a second
// copy of World's own damage/death decision - World has already committed the Alive->Dead
// transition and its Died feed entry the instant ApplyMonsterDamageAsync reported
// KilledByThisHit=true; this type only orders WHEN this one gateway process tells its OWN connected
// clients about it.
//
// The race this closes (live multiplayer regression on top of the player-attack fan-out fix -
// PlayerAttackActionOutcome): World's independently-pollable Died feed entry for a life and the
// attacking session's own local lethal-hit projection (final 0x08C8, fanned out via
// MapTcpServer.FanOutPlayerAttackActionAsync) become observable at roughly the same real time.
// MapTcpServer's own monster-tick loop can poll and fan the Died vanish out to every OTHER session
// on the map (LethalDeathProjectionArbiter already defers the ATTACKER's own session's copy of this
// exact race - see that type's own doc comment, entirely per-session) concurrently with the
// attacking session still building/sending the final action. Without this gate, an observer that
// had the monster visible could receive 0x0080 with no preceding 0x08C8 (or never receive the
// action at all): NotifyMonsterDiedAsync's own vanish removes the monster from that session's
// _visibleActorIds, so the very next thing NotifyPlayerAttackActionAsync does - its own, unrelated
// and still-correct visibility check - silently drops the now-too-late action.
//
// `Enter` is called by the attacking session BEFORE it dispatches World's ApplyMonsterDamageAsync
// RPC (mirrors LethalDeathProjectionArbiter.BeginInFlight's own "registered before the RPC that may
// report KilledByThisHit" invariant exactly - see EnterLethalInFlight's own call sites) - so no Died
// feed entry for this life can ever become pollable before this gate's own registration exists,
// closing the "gate not registered yet" window. `Exit` releases it as soon as the attacking
// session's own player-attack ACTION has been fully fanned out (never the whole lethal tail -
// EXP/progression/the vanish itself are attacker/session-local concerns the Died fan-out never
// needs to wait for - see CompleteLethalInFlight's own doc comment for exactly where each path
// releases). Keyed by the EXACT life (never ActorId alone, for the identical reason
// LethalDeathProjectionArbiter is): an unrelated monster, or even a DIFFERENT incarnation of the
// SAME ActorId, is never serialized against this one.
public sealed class LethalAttackProjectionGate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<WorldMonsterLifeReference, TaskCompletionSource> _open = [];

    // Registers that a lethal-capable local attack is starting for `life`. Idempotent-safe against a
    // lost-response retry re-dispatching the SAME logical attempt (re-Entering an already-open life
    // is a no-op - the original TaskCompletionSource, and therefore the original ordering guarantee
    // already handed out to any concurrent waiter, stays authoritative).
    public void Enter(WorldMonsterLifeReference life)
    {
        lock (_gate) { _open.TryAdd(life, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)); }
    }

    // Releases `life`'s gate, letting any Died dispatch currently waiting in WaitAsync proceed. A
    // life with no open gate (Enter was never called for it, this exact call already ran once, or a
    // concurrent Exit already ran) is a silent no-op - Exit is safe to call defensively.
    public void Exit(WorldMonsterLifeReference life)
    {
        TaskCompletionSource? source;
        lock (_gate) { if (!_open.Remove(life, out source)) return; }
        source!.TrySetResult();
    }

    // Called by MapTcpServer.FanOutEntryAsync immediately before fanning a Died entry out to
    // sessions: if `life` currently has an open gate, returns a Task that completes once it closes
    // (the caller applies its own bounded upper wait, so a stuck/disconnected attacker session can
    // never block this life's Died fan-out forever); otherwise returns an already-completed Task.
    // Never touches any OTHER life's own registration - only ever awaits the ONE TaskCompletionSource
    // for this exact life, so unrelated monsters are never serialized against each other.
    public Task WaitAsync(WorldMonsterLifeReference life)
    {
        Task task;
        lock (_gate) { task = _open.TryGetValue(life, out var source) ? source.Task : Task.CompletedTask; }
        return task;
    }

    // Diagnostics-only: lets a test assert the gate never leaks entries across many kill cycles.
    internal int OpenCountForTest { get { lock (_gate) return _open.Count; } }
}
