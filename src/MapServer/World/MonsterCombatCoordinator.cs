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
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon,
        Func<uint, CharacterQuestStatus> attackerQuestStatus)
    {
        if (!target.IsAlive) return new(false, target.CurrentHp, target.CurrentHp, false, false, []);

        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.Spawn.Mob));
        var (hpBefore, hpAfter, killed) = target.ApplyDamage(result.Damage);

        IReadOnlyList<QuestDropOutcome> drops = [];
        if (killed)
        {
            drops = questDrops.ResolveDrops(attackerQuestStatus, target.Spawn.Mob.Id);
            monsters.ScheduleRespawnIfNeeded(target);
        }

        return new(true, hpBefore, hpAfter, result.IsMiss, killed, drops);
    }
}
