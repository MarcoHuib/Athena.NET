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
