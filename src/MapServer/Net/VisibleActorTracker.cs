namespace Athena.Net.MapServer.Net;

// Thread-safe wrapper around one session's "which actor IDs has this client already been told
// exist" set. Before MapTcpServer's shared monster tick loop existed, this state was only ever
// touched from one session's own sequential packet-handling/background-loop code, so a plain
// HashSet<uint> was safe. It no longer is: MapClientSession.NotifyMonsterMovedAsync is now called
// from MapTcpServer's OWN background tick loop, concurrently with that session's packet loop
// (actor-info handling, death/vanish handling, warp/map-change Clear, repeat-attack processing)
// and its own player-movement visibility scan - several independent call sites that can race on
// the same HashSet<uint>, which is not safe to read/mutate concurrently under any circumstance
// (not just "give wrong answers" - literally undefined/corrupting behavior for concurrent
// Add/Remove).
//
// A single `lock` (not a full lock-free/concurrent-collection design) is deliberately the
// smallest correct fix here: every operation below is O(1)-ish HashSet work with no I/O, so
// holding the lock for the whole operation is cheap and never blocks on a network write -
// callers must never call these while already holding this lock or vice versa, and every method
// here returns before any WriteAsync/await happens in the caller.
//
// TryMarkVisible folds the pre-existing "Contains, then if false Add" pattern several call
// sites used into ONE atomic operation (HashSet<T>.Add already reports whether the item was
// newly added) - that combined check+add is exactly the operation that must be atomic under
// concurrency: two racing callers must never both observe "not yet visible" and both send a
// discovery packet for the same actor.
//
// Extracted to its own top-level (internal) type specifically so its concurrency invariant is
// directly, deterministically testable (VisibleActorTrackerTests) without a real TCP session, a
// background reader task, or an uncontrolled real-time hammering window - see that test file's own
// doc comment for why a mixed session-wiring/concurrency-load test was replaced with this direct
// unit plus a small separate bounded MapClientSession wiring test.
internal sealed class VisibleActorTracker
{
    private readonly Lock _gate = new();
    private readonly HashSet<uint> _actorIds = [];

    public bool IsActorVisible(uint actorId) { lock (_gate) return _actorIds.Contains(actorId); }

    // Returns true only when THIS call is the one that actually added actorId (matching
    // HashSet<T>.Add's own "returns true if the element is added" contract) - a caller uses
    // this to decide whether IT is responsible for sending the one-time discovery packet.
    public bool TryMarkVisible(uint actorId) { lock (_gate) return _actorIds.Add(actorId); }

    public void MarkNotVisible(uint actorId) { lock (_gate) _actorIds.Remove(actorId); }

    public void Clear() { lock (_gate) _actorIds.Clear(); }
}
