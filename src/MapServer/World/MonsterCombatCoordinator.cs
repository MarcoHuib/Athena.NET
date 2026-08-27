using Athena.Net.MapServer.Gameplay.Rules;

namespace Athena.Net.MapServer.World;

// Outcome of one basic-attack attempt against a live MobInstance.
public readonly record struct MonsterAttackOutcome(
    bool Accepted,
    uint HpBefore,
    uint HpAfter,
    bool IsMiss,
    bool KilledByThisHit,
    IReadOnlyList<QuestDropOutcome> QuestDrops);

// Coordinates one authoritative attack -> damage -> (exactly-once) death ->
// quest-drop -> respawn-scheduling transition against a target MobInstance.
// This is the single place death is resolved: callers never mutate
// MobInstance HP directly, so "two simultaneous lethal attacks -> one death,
// one quest-drop award" is enforced by MobInstance.ApplyDamage's own lock,
// not by caller discipline.
//
// Depends only on IBasicAttackRules - this class never knows or asks which gameplay
// ruleset (Renewal/PreRenewal) is active. The concrete implementation is selected
// once at MapServer startup by GameplayRulesFactory and threaded in here.
public sealed class MonsterCombatCoordinator(MonsterRegistry monsters, QuestDropResolver questDrops, IBasicAttackRules basicAttackRules)
{
    // `attackerQuestStatus`: a synchronous, already-resolved per-quest-ID lookup (see
    // QuestDropResolver's doc comment) - the caller must obtain each relevant quest's status from
    // ICharacterQuestPersistence BEFORE calling Attack, e.g. by awaiting GetQuestStateAsync once per
    // distinct QuestId in the generated drop rules and closing over the results.
    //
    // `equippedWeapon`: the CURRENT authoritative right-hand weapon, already resolved by the caller
    // via EquippedWeaponResolver against the session's live CharacterEquipmentSnapshot - null means
    // genuinely unarmed (EquippedWeaponResolution.Unarmed), never "unknown"/"not looked up yet". This
    // coordinator never reads equipment state itself; it just forwards it into a BasicAttackContext for
    // IBasicAttackRules, so re-equipping/unequipping mid-session changes the very next attack's
    // calculation without any coordinator-side caching to invalidate.
    public MonsterAttackOutcome Attack(
        MobInstance target,
        uint attackerAccountId,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon,
        Func<uint, CharacterQuestStatus> attackerQuestStatus)
    {
        if (!target.IsAlive) return new(false, target.CurrentHp, target.CurrentHp, false, false, []);

        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.Spawn.Mob));
        var (hpBefore, hpAfter, killed) = target.ApplyDamage(result.Damage);

        // Pinned mob_set_attacked_id, called from the walk-delay timer battle_damage schedules for
        // every hit that connects against a mob (battle.cpp:356-362) - see MobInstance.
        // TryAcquireTarget's own doc comment for why this project calls it immediately rather than
        // reproducing that intermediate timer hop. A killing hit must not re-acquire a target on an
        // instance that ApplyDamage just moved to Dead (TryAcquireTarget's own IsAlive guard already
        // makes this a no-op, but skipping the call entirely when killed also avoids the pointless
        // MSS_RUSH-on-a-dead-mob transition that TryAcquireTarget's own logic would otherwise not
        // reach anyway - either way, matches pinned mob_dead's own immediate unlock, mob.cpp:3863).
        // `allowChangeTargetWhileChasing: false` matches G_PORING's mode lacking MD_CHANGETARGETMELEE
        // /MD_CHANGETARGETCHASE (mob.cpp:1242,1252) - see TryAcquireTarget's own doc comment.
        if (!killed) target.TryAcquireTarget(attackerAccountId, allowChangeTargetWhileChasing: false);

        IReadOnlyList<QuestDropOutcome> drops = [];
        if (killed)
        {
            drops = questDrops.ResolveDrops(attackerQuestStatus, target.Spawn.Mob.Id);
            monsters.ScheduleRespawnIfNeeded(target);
        }

        return new(true, hpBefore, hpAfter, result.IsMiss, killed, drops);
    }
}
