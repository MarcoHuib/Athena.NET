using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Net;

// Item 3 of the Step 6 correctness-hardening pass: one small, synchronized owner for the
// session-local monster-visibility metadata that used to be THREE independently-mutated pieces
// (_visibleMonsterActorIds, _lastKnownMonsterIncarnations, _lastReconciledEpoch) - previously
// updated from TWO independently-scheduled call paths (SendVisibleMonsterActorsAsync, driven by
// this session's OWN packet-loop/movement handling, and ReconcileMonsterVisibilityAsync/
// NotifyMonsterMovedAsync/NotifyMonsterDiedAsync, driven by MapTcpServer's SEPARATE monster-tick
// loop) without any shared synchronization between them - a plain Dictionary<uint,
// WorldMonsterIncarnationId> mutated from two independently-scheduled callers is exactly the kind
// of race VisibleActorTracker's own doc comment already warns about for _visibleActorIds itself.
//
// Owns exactly: visible monster ActorId -> IncarnationId, and the last SimulationEpoch this
// session fully reconciled against. Every operation is a short, synchronous, in-memory
// dictionary/field operation under one `Lock` - no socket write, Orleans RPC, database call,
// packet construction, or await may EVER happen while this type's lock is held (mirroring
// VisibleActorTracker's own identical convention) - callers perform the actual wire I/O
// themselves, outside this type, using the plain values (bool/IncarnationId/epoch) these methods
// return.
internal sealed class MonsterVisibilityState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, WorldMonsterIncarnationId> _incarnationByActorId = [];
    private WorldSimulationEpoch? _lastReconciledEpoch;

    // Immutable point-in-time copy of every currently-tracked (ActorId, IncarnationId) pair - used
    // by a caller (ReconcileMonsterVisibilityAsync) that needs to diff "what did this session
    // previously believe was visible" against a fresh snapshot, without holding this type's own
    // lock across that diff loop (which itself calls back into Remove/MarkVisible for entries the
    // diff decides to vanish/rediscover - a plain `Lock` does not support re-entrant acquisition).
    public (uint ActorId, WorldMonsterIncarnationId IncarnationId)[] Snapshot()
    {
        lock (_gate) { return [.. _incarnationByActorId.Select(pair => (pair.Key, pair.Value))]; }
    }

    // Records that `actorId` is now visible to this session as `incarnationId` - called by BOTH the
    // ordinary discovery path (NotifyMonsterMovedAsync's own not-yet-visible branch) and the
    // resync/reconciliation path (ReconcileMonsterVisibilityAsync's own rediscovery loop) so every
    // path that actually exposes a life to the client records the incarnation it exposed, not only
    // full-reconciliation paths.
    public void MarkVisible(uint actorId, WorldMonsterIncarnationId incarnationId)
    {
        lock (_gate) { _incarnationByActorId[actorId] = incarnationId; }
    }

    public void Remove(uint actorId)
    {
        lock (_gate) { _incarnationByActorId.Remove(actorId); }
    }

    // True when this session currently believes `actorId` is visible AND at exactly `incarnationId`
    // - a plain ActorId match is not enough to prove "same life" (see WorldMonsterIncarnationId's
    // own doc comment); a different or absent incarnation both report false, which is exactly the
    // "must vanish the OLD life first" / "was never visible" signal ReconcileMonsterVisibilityAsync
    // needs from a single atomic check.
    public bool IsVisibleAtIncarnation(uint actorId, WorldMonsterIncarnationId incarnationId)
    {
        lock (_gate) { return _incarnationByActorId.TryGetValue(actorId, out var current) && current.Equals(incarnationId); }
    }

    // Resets ALL monster-visibility metadata for a real client-facing map transition (warp,
    // teleport, map change, re-authentication) - every existing call site that already clears the
    // generic _visibleActorIds tracker for these transitions must also clear this type, since a
    // monster ActorId from the OLD map/session lifetime must never be treated as "still visible at
    // the same incarnation" once the session has moved on (an ActorId space is per-map/per-session-
    // lifetime, not globally unique across a transition). Also clears the last-reconciled-epoch
    // memory - the new map's own projection (if any) starts this session's reconciliation fresh,
    // exactly like a session's very first bootstrap.
    public void Reset()
    {
        lock (_gate)
        {
            _incarnationByActorId.Clear();
            _lastReconciledEpoch = null;
        }
    }

    // Atomically compares `epoch` against the last epoch this session fully reconciled against AND
    // records `epoch` as the new last-reconciled value - returns true when `epoch` is different
    // from what was previously recorded (including the very first reconciliation, where there was
    // no previous value at all), matching ReconcileMonsterVisibilityAsync's own "epoch changed since
    // last reconciliation -> everything this session had visible under the old epoch is stale"
    // rule. Combining the compare and the update into one call (rather than a separate get then a
    // separate set) closes exactly the kind of split-read/write race this type exists to eliminate.
    public bool CompareAndUpdateReconciledEpoch(WorldSimulationEpoch epoch)
    {
        lock (_gate)
        {
            var changed = _lastReconciledEpoch is not { } last || !last.Equals(epoch);
            _lastReconciledEpoch = epoch;
            return changed;
        }
    }
}
