using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// REAL key shape (Step 6, post-World-cutover) - the full authoritative life identity every World
// mutation already validates against: MapId + SimulationEpoch + ActorId + IncarnationId. This
// replaced Step 5's deliberately temporary (MapId, ActorId, IncarnationId) key now that MapServer
// actually bootstraps combat state from the real World feed/grain and has a genuine, grain-issued
// WorldSimulationEpoch to key by - never a locally-synthesized epoch (see WorldSimulationEpoch's
// own doc comment: it is authoritative World identity).
public readonly record struct MonsterCombatKey(string MapId, WorldSimulationEpoch Epoch, uint ActorId, WorldMonsterIncarnationId IncarnationId)
{
    public static MonsterCombatKey From(WorldMonsterLifeReference reference) =>
        new(reference.MapId, reference.SimulationEpoch, reference.ActorId, reference.IncarnationId);
}

// Substep 9 cutover: World is the sole authority for monster CurrentHp/MaxHp/lethality (via
// ApplyMonsterDamageAsync) - this store no longer mirrors HP locally at all. It owns exactly one
// thing now: NextAttackAt cadence bookkeeping for the local attack-cadence executor
// (MonsterAttackCadenceExecutor) and, historically, MapClientSession's own repeat-attack loop
// (which as of substep 9 no longer reads HP from here either - see PendingMonsterDamageAttempt in
// MapClientSession.cs for the live player-attack path's own damage state, which now lives entirely
// in the pending-attempt/World-RPC round trip, never in this store).
//
// Per-key locking (a single `Lock` guarding the whole dictionary, not one lock per entry) - entries
// are created/removed by the same Reconcile path that also mutates cadence, so a per-entry lock
// object would itself need dictionary-level synchronization to safely hand out/replace - not worth
// the added complexity for this store's actual concurrency profile (bounded per-map monster counts,
// not a high-contention hot path).
public sealed class MonsterAttackCadenceStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<MonsterCombatKey, MonsterAttackCadenceState> _byKey = [];

    // Registers (or re-registers, e.g. after a respawn/new incarnation, or the FIRST time a life is
    // observed via bootstrap/resync) the cadence entry for one monster life - no scheduled attack.
    // Never speculatively "upserted" by a cadence call, so a caller can never silently create an
    // entry for a life that was never actually registered. Idempotent for the SAME key
    // (re-registering an already-registered life resets its NextAttackAt to null) - callers must
    // only call this for a life that is genuinely fresh from the store's own perspective; see
    // MonsterFeedProjection's own reconciliation rules for exactly when this is correct to call:
    // new incarnation, new epoch rebuild, or first-time bootstrap observation of an
    // ALREADY-DEAD-in-World life, which still gets a nominal entry rather than none at all.
    public void Register(string mapId, WorldSimulationEpoch epoch, uint actorId, WorldMonsterIncarnationId incarnationId)
    {
        var key = new MonsterCombatKey(mapId, epoch, actorId, incarnationId);
        lock (_gate)
        {
            _byKey[key] = new MonsterAttackCadenceState(actorId, incarnationId, NextAttackAt: null);
        }
    }

    // Read-only snapshot for cadence evaluation - returns false for an unregistered key OR a stale
    // epoch/incarnation (the entry belongs to a DIFFERENT map epoch or an older incarnation - e.g.
    // this monster has since respawned or the map's simulation was rebuilt under a new epoch this
    // caller does not yet know about), never a silently-mismatched value.
    public bool TryGet(MonsterCombatKey key, out MonsterAttackCadenceState state)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var found))
            {
                state = found;
                return true;
            }
        }
        state = null!;
        return false;
    }

    public bool TryGet(WorldMonsterLifeReference reference, out MonsterAttackCadenceState state) => TryGet(MonsterCombatKey.From(reference), out state);

    // Cadence mutation - the ONLY place NextAttackAt is written. A stale/unregistered key is a
    // silent no-op (a missed schedule has no double-application risk, only a slightly-early next
    // evaluation, which the next tick's own re-evaluation self-corrects).
    public void ScheduleNextAttack(MonsterCombatKey key, DateTimeOffset dueAt)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var entry))
                _byKey[key] = entry with { NextAttackAt = dueAt };
        }
    }

    // Explicit cleanup APIs (requirement: "add explicit map/epoch cleanup APIs rather than leaving
    // stale entries indefinitely") - called by MonsterFeedProjection's own reconciliation:
    //   - RemoveEpoch: a map's SimulationEpoch changed (the World simulation was rebuilt) - every
    //     entry under the OLD epoch for that map is unreachable/stale and must be discarded outright,
    //     never merged with the new epoch's fresh snapshot.
    //   - Remove: a single life ended (Died, with no further tracking needed) or was superseded by
    //     a specific new incarnation - removes exactly that one key, leaving every other life
    //     (including a same-map, same-epoch, different-ActorId life) untouched.
    public void RemoveEpoch(string mapId, WorldSimulationEpoch epoch)
    {
        lock (_gate)
        {
            foreach (var key in _byKey.Keys.Where(k => string.Equals(k.MapId, mapId, StringComparison.OrdinalIgnoreCase) && k.Epoch.Equals(epoch)).ToArray())
                _byKey.Remove(key);
        }
    }

    public void Remove(MonsterCombatKey key)
    {
        lock (_gate) { _byKey.Remove(key); }
    }
}
