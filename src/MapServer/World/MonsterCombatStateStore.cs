namespace Athena.Net.MapServer.World;

// TEMPORARY key shape (Step 5, pre-World-cutover) - MapId + ActorId + IncarnationId only.
//
// This is intentionally NOT the final distributed identity. Once MapServer actually bootstraps
// combat state from the real World feed/grain (a later step, not this one), this key MUST be
// extended to the full authoritative tuple:
//
//     MapId + SimulationEpoch + ActorId + IncarnationId
//
// Do NOT read this as "the store is missing SimulationEpoch by oversight" - a locally-generated
// Guid standing in for SimulationEpoch here would be actively wrong (it could be confused with
// World's own real, grain-issued SimulationEpoch, which does not exist in MapServer's own process
// at all yet - MapServer has no Orleans/grain client on this path). SimulationEpoch is authoritative
// World identity and must only enter MapServer when the real bootstrap/feed is wired.
public readonly record struct MonsterCombatKey(string MapId, uint ActorId, MonsterIncarnationId IncarnationId);

// The authoritative MapServer-LOCAL owner of CurrentHp/NextAttackAt on the migrated live combat
// path (MonsterCombatCoordinator, MonsterEngagementTickProcessor, and every production call site
// they depend on) - see this project's own Phase 2B plan for the full authority-boundary rationale
// (position/movement/identity stay IMonsterActorView-shaped, reading from MobInstance; combat
// cadence/HP live here instead). MobInstance's OWN CurrentHp/ApplyDamage/NextAttackAt/
// ScheduleNextAttack/CaptureCombatState members are superseded on this path - see each of their
// own doc comments - and retained only for their existing unit tests / any not-yet-migrated caller.
// There is exactly ONE mutable HP/cadence authority reachable from production combat code: this
// store.
//
// Per-key locking (a single `Lock` guarding the whole dictionary, not one lock per entry) is the
// serialization point for the "two simultaneous lethal hits -> exactly one death" guarantee that
// used to live inside MobInstance.ApplyDamage's own lock - see ApplyDamage's own doc comment below
// for the exact sequencing this preserves. A single dictionary-wide lock (rather than a genuinely
// separate lock per key) is deliberate: entries are created/removed by the same Rebuild/Register
// path that also mutates HP, so a per-entry lock object would itself need dictionary-level
// synchronization to safely hand out/replace - not worth the added complexity for this store's
// actual concurrency profile (bounded per-map monster counts, not a high-contention hot path).
public sealed class MonsterCombatStateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<MonsterCombatKey, MonsterCombatState> _byKey = [];

    // Registers (or re-registers, e.g. after a respawn/new incarnation) the combat-state entry for
    // one MobInstance's CURRENT incarnation - fresh full HP, no scheduled attack. Called once at
    // initial spawn construction and once per successful respawn (see MonsterRegistry's own call
    // sites) - never speculatively "upserted" by a damage/cadence call, so a caller can never
    // silently create a combat-state entry for an incarnation that was never actually registered.
    public void Register(string mapId, MobInstance instance)
    {
        var key = new MonsterCombatKey(mapId, instance.ActorId, instance.IncarnationId);
        lock (_gate)
        {
            _byKey[key] = new MonsterCombatState(instance.ActorId, instance.IncarnationId, instance.Spawn.Mob.MaxHp, instance.Spawn.Mob.MaxHp, NextAttackAt: null);
        }
    }

    // Read-only snapshot for packet projection/diagnostics - returns false for an unregistered key
    // OR a stale incarnation (the entry exists under a DIFFERENT, older IncarnationId - e.g. this
    // instance has since respawned into a new life this caller does not yet know about), never a
    // silently-mismatched value.
    public bool TryGet(string mapId, uint actorId, MonsterIncarnationId incarnationId, out MonsterCombatState state)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(new MonsterCombatKey(mapId, actorId, incarnationId), out var found))
            {
                state = found;
                return true;
            }
        }
        state = null!;
        return false;
    }

    // Convenience overload for the common "I already have the live MobInstance, give me its
    // CURRENT combat state" read (used by packet projection) - resolves the key from the instance's
    // own current ActorId/IncarnationId, so a caller never has to separately track which incarnation
    // it last saw.
    public bool TryGet(string mapId, MobInstance instance, out MonsterCombatState state) =>
        TryGet(mapId, instance.ActorId, instance.IncarnationId, out state);

    // The single production damage-application entry point on the migrated combat path - replaces
    // MobInstance.ApplyDamage for MonsterCombatCoordinator/MonsterEngagementTickProcessor. Atomic
    // sequence, entirely inside this store's own lock:
    //   1. Resolve the entry for (mapId, actorId, incarnationId) - StaleIncarnation if it does not
    //      exist under that exact incarnation (either never registered, or the mob has since
    //      respawned into a newer life this caller's `incarnationId` argument does not name).
    //   2. Validate `instanceForLifecycle` is genuinely still alive - a hit resolving to a stale
    //      key that ALSO happens to no longer be alive (respawned/redied since) is caught by (1)'s
    //      incarnation check first in the normal case, but this is checked explicitly too as a
    //      defensive invariant, never assumed.
    //   3. Clamp damage against the entry's OWN CurrentHp (never MobInstance's own now-superseded
    //      field).
    //   4. Store the updated entry.
    //   5. Determine whether THIS call performed the HP>0 -> HP==0 transition.
    //   6. If and only if so, call MobInstance.MarkDeadIfNeeded() - NEVER MobInstance.ApplyDamage -
    //      while STILL holding this store's own lock, so a second concurrent lethal call for the
    //      SAME key is fully serialized behind this one and can only ever observe the
    //      already-zeroed HP with KilledByThisHit=false.
    //
    // Lock-order note: this call takes the store's OWN lock, then (only on the lethal branch) calls
    // into MobInstance's own separate `_gate` via MarkDeadIfNeeded - never the reverse (no
    // MobInstance method calls back into this store while holding `_gate`), so no lock-order
    // inversion is possible between this store and MobInstance.
    public MonsterCombatDamageResult ApplyDamage(string mapId, MobInstance instanceForLifecycle, MonsterIncarnationId incarnationId, uint damage)
    {
        lock (_gate)
        {
            var key = new MonsterCombatKey(mapId, instanceForLifecycle.ActorId, incarnationId);
            if (!_byKey.TryGetValue(key, out var entry))
                return MonsterCombatDamageResult.StaleIncarnation;
            if (!instanceForLifecycle.IsAlive)
                return new MonsterCombatDamageResult(MonsterCombatDamageStatus.AlreadyDead, entry.CurrentHp, entry.CurrentHp, KilledByThisHit: false);

            var before = entry.CurrentHp;
            var after = damage >= before ? 0u : before - damage;
            _byKey[key] = entry with { CurrentHp = after };

            var killedByThisHit = after == 0 && before > 0 && instanceForLifecycle.MarkDeadIfNeeded();
            return new MonsterCombatDamageResult(MonsterCombatDamageStatus.Applied, before, after, killedByThisHit);
        }
    }

    // Cadence mutation, same per-key ownership model as ApplyDamage - the ONLY place NextAttackAt
    // is written on the migrated combat path. A stale/unregistered key is a silent no-op (mirrors
    // ApplyDamage's own explicit StaleIncarnation reporting being unnecessary here since no caller
    // currently needs to distinguish "schedule silently ignored" from "scheduled" for cadence -
    // unlike damage, a missed schedule has no double-application risk, only a slightly-early next
    // evaluation, which the next tick's own re-evaluation self-corrects).
    public void ScheduleNextAttack(string mapId, uint actorId, MonsterIncarnationId incarnationId, DateTimeOffset dueAt)
    {
        lock (_gate)
        {
            var key = new MonsterCombatKey(mapId, actorId, incarnationId);
            if (_byKey.TryGetValue(key, out var entry))
                _byKey[key] = entry with { NextAttackAt = dueAt };
        }
    }
}

public enum MonsterCombatDamageStatus { Applied, StaleIncarnation, AlreadyDead }

public readonly record struct MonsterCombatDamageResult(MonsterCombatDamageStatus Status, uint HpBefore, uint HpAfter, bool KilledByThisHit)
{
    public static readonly MonsterCombatDamageResult StaleIncarnation = new(MonsterCombatDamageStatus.StaleIncarnation, 0, 0, false);
}
