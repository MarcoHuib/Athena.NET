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

// The authoritative MapServer-LOCAL owner of CurrentHp/NextAttackAt on the live combat path
// (MonsterCombatCoordinator, the local attack-cadence executor, and every production call site
// they depend on) - see this project's own Phase 2B plan for the full authority-boundary rationale
// (position/movement/identity/lifecycle/engagement/chase/respawn timing are all World-authoritative,
// read via IMonsterActorView/WorldMonsterInstance; combat cadence/HP/damage-calculation/quest-drop
// stay MapServer-local, live here). Post-cutover, there is no local MobInstance for a production
// monster at all - this store's own damage/cadence mutations never call into a MobInstance
// lifecycle method; a lethal transition is reported back to the caller as a plain fact (HP reached
// zero) and it is the ORCHESTRATION layer's job to then call World's TryMarkMonsterDeadAsync - see
// that RPC's own doc comment for why World, not this store, owns the authoritative Alive->Dead
// transition and its respawn scheduling.
//
// Per-key locking (a single `Lock` guarding the whole dictionary, not one lock per entry) is the
// serialization point for "two simultaneous lethal hits -> exactly one HP==0 report" - see
// ApplyDamage's own doc comment for the exact sequencing. A single dictionary-wide lock (rather
// than a genuinely separate lock per key) is deliberate: entries are created/removed by the same
// Reconcile path that also mutates HP, so a per-entry lock object would itself need
// dictionary-level synchronization to safely hand out/replace - not worth the added complexity for
// this store's actual concurrency profile (bounded per-map monster counts, not a high-contention
// hot path).
public sealed class MonsterCombatStateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<MonsterCombatKey, MonsterCombatState> _byKey = [];

    // Registers (or re-registers, e.g. after a respawn/new incarnation, or the FIRST time a life is
    // observed via bootstrap/resync) the combat-state entry for one monster life - fresh full HP,
    // no scheduled attack. Never speculatively "upserted" by a damage/cadence call, so a caller can
    // never silently create a combat-state entry for a life that was never actually registered.
    // Idempotent for the SAME key (re-registering an already-registered life resets it to full HP -
    // callers must only call this for a life that is genuinely fresh from the store's own
    // perspective; see MonsterFeedProjection's own reconciliation rules for exactly when this is
    // correct to call: new incarnation, new epoch rebuild, or first-time bootstrap observation of
    // an ALREADY-DEAD-in-World life, which still gets a nominal zeroed entry rather than none at all).
    public void Register(string mapId, WorldSimulationEpoch epoch, uint actorId, WorldMonsterIncarnationId incarnationId, uint maxHp)
    {
        var key = new MonsterCombatKey(mapId, epoch, actorId, incarnationId);
        lock (_gate)
        {
            _byKey[key] = new MonsterCombatState(actorId, incarnationId, maxHp, maxHp, NextAttackAt: null);
        }
    }

    // Read-only snapshot for packet projection/diagnostics/cadence evaluation - returns false for
    // an unregistered key OR a stale epoch/incarnation (the entry belongs to a DIFFERENT map epoch
    // or an older incarnation - e.g. this monster has since respawned or the map's simulation was
    // rebuilt under a new epoch this caller does not yet know about), never a silently-mismatched
    // value.
    public bool TryGet(MonsterCombatKey key, out MonsterCombatState state)
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

    public bool TryGet(WorldMonsterLifeReference reference, out MonsterCombatState state) => TryGet(MonsterCombatKey.From(reference), out state);

    // The single production damage-application entry point on the live combat path - atomic
    // sequence, entirely inside this store's own lock:
    //   1. Resolve the entry for the exact key - StaleLife if it does not exist under that exact
    //      (MapId, Epoch, ActorId, IncarnationId) (either never registered, already reaped by an
    //      epoch/incarnation cleanup, or the caller's own reference is simply out of date).
    //   2. AlreadyDead if the entry's own CurrentHp is already 0 (a hit against an already-dead
    //      life, e.g. a second concurrent lethal hit or a stale-but-not-yet-reconciled attacker).
    //   3. Clamp damage against the entry's OWN CurrentHp.
    //   4. Store the updated entry.
    //   5. Report whether THIS call performed the HP>0 -> HP==0 transition - the caller (orchestration)
    //      is responsible for calling World's TryMarkMonsterDeadAsync when this is true; this store
    //      itself never calls into World and never mutates any MobInstance-shaped lifecycle state
    //      (there is none to mutate post-cutover).
    //
    // Retained for callers that intend to commit unconditionally (nothing awaits World between
    // "calculate" and "commit" for them) - see TryCommitDamage below for the fail-closed, CAS-style
    // variant the orchestration layer now uses whenever a World RPC confirmation must land between
    // computing candidate damage and committing it (item 2 of the Step 6 correctness-hardening pass:
    // "do not mutate local HP before World confirms the attack").
    public MonsterCombatDamageResult ApplyDamage(MonsterCombatKey key, uint damage)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var entry))
                return MonsterCombatDamageResult.StaleLife;
            if (entry.CurrentHp == 0)
                return new MonsterCombatDamageResult(MonsterCombatDamageStatus.AlreadyDead, 0, 0, KilledByThisHit: false);

            var before = entry.CurrentHp;
            var after = damage >= before ? 0u : before - damage;
            _byKey[key] = entry with { CurrentHp = after };
            return new MonsterCombatDamageResult(MonsterCombatDamageStatus.Applied, before, after, KilledByThisHit: after == 0);
        }
    }

    // Read-only candidate-damage helper - reports what ApplyDamage/TryCommitDamage would currently
    // see (StaleLife/AlreadyDead/the CurrentHp a damage roll should be computed against) WITHOUT
    // mutating anything. Exists so a caller can calculate the damage formula's own result using the
    // CurrentHp this returns, then await an external confirmation (a World RPC), then commit via
    // TryCommitDamage below using THIS SAME CurrentHp as the expected pre-image - never mutating
    // while any await is outstanding, and never holding this store's lock across that await.
    public MonsterCombatPeekResult Peek(MonsterCombatKey key)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var entry))
                return MonsterCombatPeekResult.StaleLife;
            return entry.CurrentHp == 0
                ? MonsterCombatPeekResult.AlreadyDead
                : new MonsterCombatPeekResult(MonsterCombatDamageStatus.Applied, entry.CurrentHp);
        }
    }

    // CAS-style commit: applies `damage` against the entry's CurrentHp ONLY IF it still equals
    // `expectedCurrentHp` (the pre-image the caller's own Peek/damage-formula call observed earlier,
    // BEFORE it awaited an external confirmation such as NotifyMonsterAttackedAsync). If a
    // concurrent hit already changed CurrentHp in the meantime (same-process concurrent-hit race -
    // e.g. two sessions attacking the same monster) this returns Conflict and applies NOTHING; the
    // caller re-Peeks, recomputes damage against the fresh CurrentHp, and retries its own external
    // confirmation/commit sequence rather than silently applying a damage roll computed against
    // stale HP. Never awaits - entirely synchronous, inside this store's own lock, exactly like
    // ApplyDamage above; the only difference is the extra compare-before-swap against
    // expectedCurrentHp.
    public MonsterCombatDamageResult TryCommitDamage(MonsterCombatKey key, uint expectedCurrentHp, uint damage)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var entry))
                return MonsterCombatDamageResult.StaleLife;
            if (entry.CurrentHp == 0)
                return new MonsterCombatDamageResult(MonsterCombatDamageStatus.AlreadyDead, 0, 0, KilledByThisHit: false);
            if (entry.CurrentHp != expectedCurrentHp)
                return new MonsterCombatDamageResult(MonsterCombatDamageStatus.Conflict, entry.CurrentHp, entry.CurrentHp, KilledByThisHit: false);

            var before = entry.CurrentHp;
            var after = damage >= before ? 0u : before - damage;
            _byKey[key] = entry with { CurrentHp = after };
            return new MonsterCombatDamageResult(MonsterCombatDamageStatus.Applied, before, after, KilledByThisHit: after == 0);
        }
    }

    // Cadence mutation, same per-key ownership model as ApplyDamage - the ONLY place NextAttackAt
    // is written on the live combat path. A stale/unregistered key is a silent no-op (a missed
    // schedule has no double-application risk, only a slightly-early next evaluation, which the
    // next tick's own re-evaluation self-corrects).
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

// Conflict: TryCommitDamage's own CAS pre-image check failed - a concurrent hit already changed
// CurrentHp between the caller's Peek and this commit attempt. Not a failure of the attacker's
// authority (unlike StaleLife/AlreadyDead) - the caller should re-Peek and retry its own
// calculate -> confirm -> commit sequence against the fresh CurrentHp.
public enum MonsterCombatDamageStatus { Applied, StaleLife, AlreadyDead, Conflict }

public readonly record struct MonsterCombatDamageResult(MonsterCombatDamageStatus Status, uint HpBefore, uint HpAfter, bool KilledByThisHit)
{
    public static readonly MonsterCombatDamageResult StaleLife = new(MonsterCombatDamageStatus.StaleLife, 0, 0, false);
}

// Peek's own read-only result shape - deliberately narrower than MonsterCombatDamageResult (no
// HpAfter/KilledByThisHit, since nothing was mutated). CurrentHp is only meaningful when Status is
// Applied (Peek's own "not stale, not already dead" case) - it is the exact pre-image a subsequent
// TryCommitDamage call must pass as `expectedCurrentHp`.
public readonly record struct MonsterCombatPeekResult(MonsterCombatDamageStatus Status, uint CurrentHp)
{
    public static readonly MonsterCombatPeekResult StaleLife = new(MonsterCombatDamageStatus.StaleLife, 0);
    public static readonly MonsterCombatPeekResult AlreadyDead = new(MonsterCombatDamageStatus.AlreadyDead, 0);
}
