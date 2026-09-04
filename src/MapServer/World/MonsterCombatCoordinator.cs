using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Outcome of one player -> monster basic-attack attempt. `EngagementAcquired` reports whether
// this hit is the caller's own local signal to call World's NotifyMonsterAttackedAsync afterward -
// this coordinator itself never calls into World (stays Orleans-free, see this type's own doc
// comment below); the orchestration layer around it (MapClientSession) reacts to this flag.
public readonly record struct MonsterAttackOutcome(
    bool Accepted,
    uint HpBefore,
    uint HpAfter,
    bool IsMiss,
    bool KilledByThisHit,
    bool EngagementAcquired,
    IReadOnlyList<QuestDropOutcome> QuestDrops);

// A CALCULATED-BUT-NOT-YET-COMMITTED candidate hit - see MonsterCombatCoordinator.CalculateAttack's
// own doc comment for why this exists as a separate step from the outcome above. `ExpectedCurrentHp`
// is the exact pre-image MonsterCombatStateStore.Peek observed - the caller must pass it unchanged
// to TryCommitDamage/CommitAttack so the store's own CAS check can detect a concurrent hit that
// landed while this candidate's own confirmation (a World RPC) was in flight.
public readonly record struct MonsterAttackCandidate(
    bool Attackable,
    uint ExpectedCurrentHp,
    uint Damage,
    bool IsMiss,
    bool WouldBeLethal,
    bool WouldAcquireEngagement);

// Step 6 cutover: coordinates one player -> monster damage calculation against MapServer-LOCAL
// combat state only (MonsterCombatStateStore) - target identity/position/lifecycle are all
// World-authoritative (per the approved Phase 2B boundary) and are supplied to this type as
// already-resolved values (an IMonsterActorView for static/position data the damage formula needs,
// a WorldMonsterLifeReference for the exact life this hit targets), never as a live MobInstance.
//
// This coordinator remains entirely Orleans/World-contract-free (only WorldMonsterLifeReference/
// WorldMonsterIncarnationId VALUE TYPES cross this boundary, never a grain reference or IWorldRuntime)
// and no longer performs any authoritative LOCAL mutation of target/engagement/respawn state:
//   - TryAcquireTarget (MobInstance's own local engagement mutation) is GONE - World's own
//     NotifyMonsterAttackedAsync is the sole authority for target acquisition now. This
//     coordinator's own EngagementAcquired flag is a pure LOCAL signal ("this hit was non-lethal
//     and landed against a CanAttack-capable mob") the orchestration layer uses to decide whether
//     to call that RPC - it carries no engagement-state mutation of its own.
//   - ScheduleRespawnIfNeeded (MonsterRegistry's own local respawn scheduling) is GONE - World owns
//     respawn timing entirely; the orchestration layer calls TryMarkMonsterDeadAsync on a lethal
//     hit and World's own respawn machinery takes over from there.
//
// Depends only on IBasicAttackRules - this class never knows or asks which gameplay ruleset
// (Renewal/PreRenewal) is active.
public sealed class MonsterCombatCoordinator(QuestDropResolver questDrops, IBasicAttackRules basicAttackRules, MonsterCombatStateStore combatState)
{
    // Item 2 of the Step 6 correctness-hardening pass: "the local HP mutation must not happen
    // before NotifyMonsterAttackedAsync confirmation - a World-rejected non-lethal hit must not
    // leave invisible local HP damage behind." CalculateAttack computes the damage formula's result
    // and the resulting Attackable/WouldBeLethal/WouldAcquireEngagement facts WITHOUT mutating
    // combatState at all (MonsterCombatStateStore.Peek is read-only) - the caller then awaits
    // whatever external confirmation is required (World's NotifyMonsterAttackedAsync for an
    // engagement-requiring hit) and only calls CommitAttack afterward, passing this candidate's own
    // ExpectedCurrentHp back so the store's CAS-style TryCommitDamage can detect a same-process
    // concurrent hit that changed CurrentHp while the confirmation RPC was in flight - never awaits
    // while any lock is held (MonsterCombatStateStore's own lock is only ever acquired for the
    // duration of one synchronous Peek/TryCommitDamage call, never spanning this method's own gap).
    public MonsterAttackCandidate CalculateAttack(
        WorldMonsterActorView target,
        WorldMonsterLifeReference life,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon)
    {
        var key = MonsterCombatKey.From(life);
        var peek = combatState.Peek(key);
        if (peek.Status != MonsterCombatDamageStatus.Applied) return default; // Attackable: false - StaleLife/AlreadyDead, nothing to calculate against.

        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.StaticMob));
        var wouldBeLethal = !result.IsMiss && result.Damage >= peek.CurrentHp;
        var wouldAcquireEngagement = !wouldBeLethal && target.StaticMob.Mode.HasFlag(MobMode.CanAttack);
        return new MonsterAttackCandidate(Attackable: true, peek.CurrentHp, result.Damage, result.IsMiss, wouldBeLethal, wouldAcquireEngagement);
    }

    // Commits a candidate CalculateAttack already computed, via the store's own CAS-style
    // TryCommitDamage - Conflict (a concurrent hit landed while the caller's own confirmation was in
    // flight) is surfaced as `Accepted: false` exactly like StaleLife/AlreadyDead: the caller is
    // expected to treat any non-Applied commit result as "this attempt did not happen", never retry
    // automatically inside this method (retrying belongs to the caller's own repeat-attack loop,
    // which will simply re-evaluate from scratch on its next scheduled attempt).
    public MonsterAttackOutcome CommitAttack(MonsterAttackCandidate candidate, WorldMonsterLifeReference life, WorldMonsterActorView target, Func<uint, CharacterQuestStatus> attackerQuestStatus)
    {
        if (!candidate.Attackable) return new(false, 0, 0, false, false, false, []);
        var key = MonsterCombatKey.From(life);
        var damageResult = combatState.TryCommitDamage(key, candidate.ExpectedCurrentHp, candidate.Damage);
        if (damageResult.Status != MonsterCombatDamageStatus.Applied)
            return new(false, damageResult.HpBefore, damageResult.HpAfter, false, false, false, []);

        var killed = damageResult.KilledByThisHit;
        var engagementAcquired = !killed && target.StaticMob.Mode.HasFlag(MobMode.CanAttack);
        IReadOnlyList<QuestDropOutcome> drops = killed ? questDrops.ResolveDrops(attackerQuestStatus, target.MobId) : [];
        return new(true, damageResult.HpBefore, damageResult.HpAfter, candidate.IsMiss, killed, engagementAcquired, drops);
    }

    public async Task<MonsterAttackOutcome> CommitAttackAsync(MonsterAttackCandidate candidate, WorldMonsterLifeReference life, WorldMonsterActorView target, Func<Task<Func<uint, CharacterQuestStatus>>> resolveQuestStates)
    {
        if (!candidate.Attackable) return new(false, 0, 0, false, false, false, []);
        var key = MonsterCombatKey.From(life);
        var damageResult = combatState.TryCommitDamage(key, candidate.ExpectedCurrentHp, candidate.Damage);
        if (damageResult.Status != MonsterCombatDamageStatus.Applied)
            return new(false, damageResult.HpBefore, damageResult.HpAfter, false, false, false, []);

        var killed = damageResult.KilledByThisHit;
        var engagementAcquired = !killed && target.StaticMob.Mode.HasFlag(MobMode.CanAttack);
        IReadOnlyList<QuestDropOutcome> drops = [];
        if (killed)
        {
            var attackerQuestStatus = await resolveQuestStates();
            drops = questDrops.ResolveDrops(attackerQuestStatus, target.MobId);
        }
        return new(true, damageResult.HpBefore, damageResult.HpAfter, candidate.IsMiss, killed, engagementAcquired, drops);
    }

    // `target`: the World-projected actor view (position/static mob data) for the monster being
    // attacked - NOT its live position authority (there is none locally), purely a read of
    // already-current World-projected data the damage formula needs (target.StaticMob).
    // `life`: the exact (MapId, SimulationEpoch, ActorId, IncarnationId) this hit targets - the
    // combat-state key. `attackerQuestStatus`: a synchronous, already-resolved per-quest-ID lookup
    // (see QuestDropResolver's doc comment). `equippedWeapon`: the CURRENT authoritative right-hand
    // weapon, already resolved by the caller.
    public MonsterAttackOutcome Attack(
        WorldMonsterActorView target,
        WorldMonsterLifeReference life,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon,
        Func<uint, CharacterQuestStatus> attackerQuestStatus)
    {
        var key = MonsterCombatKey.From(life);
        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.StaticMob));
        var damageResult = combatState.ApplyDamage(key, result.Damage);
        if (damageResult.Status != MonsterCombatDamageStatus.Applied)
            return new(false, damageResult.HpBefore, damageResult.HpAfter, false, false, false, []);
        var (hpBefore, hpAfter, killed) = (damageResult.HpBefore, damageResult.HpAfter, damageResult.KilledByThisHit);

        // Pinned mob_ai_sub_hard's own target-acquisition gate ("if (md->attacked_id &&
        // mode&MD_CANATTACK)", mob.cpp:1937): a mob without MD_CANATTACK never promotes an attacker
        // into a combat target at all - this remains a purely LOCAL signal of whether the
        // orchestration layer should even attempt NotifyMonsterAttackedAsync; World's own
        // NotifyMonsterAttackedAsync independently re-validates MobMode.CanAttack itself (see that
        // RPC's own doc comment) - this check here is just an optimization to skip a doomed-to-fail
        // RPC call, not the authoritative gate.
        var engagementAcquired = !killed && target.StaticMob.Mode.HasFlag(MobMode.CanAttack);

        IReadOnlyList<QuestDropOutcome> drops = killed ? questDrops.ResolveDrops(attackerQuestStatus, target.MobId) : [];
        return new(true, hpBefore, hpAfter, result.IsMiss, killed, engagementAcquired, drops);
    }

    // Section 15's own optimization: a quest-state CharServer roundtrip is only ever NEEDED when
    // THIS hit actually kills the target - resolving every distinct QuestId's state on EVERY
    // ordinary non-lethal hit is pure waste. This overload defers `resolveQuestStates` (an async
    // ICharacterQuestPersistence-backed resolver) until AFTER ApplyDamage has already determined
    // `killed` atomically - MonsterCombatStateStore.ApplyDamage's own per-key lock still enforces
    // "two simultaneous lethal hits -> one HP==0 report" (see that method's own doc comment); this
    // method only decides WHETHER to await the resolver at all, never races the death determination.
    public async Task<MonsterAttackOutcome> AttackAsync(
        WorldMonsterActorView target,
        WorldMonsterLifeReference life,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon,
        Func<Task<Func<uint, CharacterQuestStatus>>> resolveQuestStates)
    {
        var key = MonsterCombatKey.From(life);
        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.StaticMob));
        var damageResult = combatState.ApplyDamage(key, result.Damage);
        if (damageResult.Status != MonsterCombatDamageStatus.Applied)
            return new(false, damageResult.HpBefore, damageResult.HpAfter, false, false, false, []);
        var (hpBefore, hpAfter, killed) = (damageResult.HpBefore, damageResult.HpAfter, damageResult.KilledByThisHit);

        var engagementAcquired = !killed && target.StaticMob.Mode.HasFlag(MobMode.CanAttack);

        IReadOnlyList<QuestDropOutcome> drops = [];
        if (killed)
        {
            var attackerQuestStatus = await resolveQuestStates();
            drops = questDrops.ResolveDrops(attackerQuestStatus, target.MobId);
        }

        return new(true, hpBefore, hpAfter, result.IsMiss, killed, engagementAcquired, drops);
    }
}
