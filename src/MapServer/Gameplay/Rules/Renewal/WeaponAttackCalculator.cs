using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules.Renewal;

// Pure RENEWAL PC basic-melee-damage math, extracted from RenewalBasicAttackRules so
// the formula stays independently testable. This is NOT itself the IBasicAttackRules
// implementation - RenewalBasicAttackRules is - it is the Renewal-only low-level
// helper that implementation delegates to. See RenewalBasicAttackRules for the full
// pinned-source call-chain citations; this file only re-states the two formula
// entry points (armed and unarmed share one pipeline in the pinned source - see
// battle.cpp:4140-4142's `if (sd)` branch, which does not test whether a weapon is
// equipped).
internal static class WeaponAttackCalculator
{
    // `rollWeaponAtk` abstracts rAthena's `rnd_value(atkmin, atkmax)` (inclusive) so
    // tests can pin the weapon-ATK roll deterministically; production passes the
    // real inclusive-random default. `weapon` is null for a confirmed-unarmed right
    // hand - battle_calc_base_weapon_attack's own `if (sd && sd->equip_index[type]
    // >= 0 ...)` guard (battle.cpp:2453) is exactly this null check: when false,
    // atkmin=atkmax=status->watk=0 (an unarmed PC's rhw.atk is never populated by
    // the equipment-parse loop), so weaponAtk collapses to 0 with no separate
    // formula needed.
    public static BasicAttackDamageResult Calculate(
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? weapon,
        MobDefinition target,
        Func<int, int, int>? rollWeaponAtk = null)
    {
        rollWeaponAtk ??= (min, max) => min >= max ? min : Random.Shared.Next(min, max + 1);

        var batk = BaseAttack(attacker, attackerBaseLevel);
        var statusAtk = 2L * batk; // battle_calc_damage_parts: wd->statusAtk *= 2 (battle.cpp:3911).

        long weaponAtk = 0;
        if (weapon is not null)
        {
            var watk = weapon.Attack; // status_calc_watk no-op: no active watk-modifying status.
            var variance = 5.0 * watk * weapon.WeaponLevel / 100.0;
            var baseStatBonus = watk * attacker.Strength / 200.0;
            var atkmin = Math.Max(0, (int)(watk - variance + baseStatBonus));
            var atkmax = Math.Min(ushort.MaxValue, (int)(watk + variance + baseStatBonus));
            if (atkmin > atkmax) atkmin = atkmax; // Defensive: pinned code never violates this for real data.

            weaponAtk = rollWeaponAtk(atkmin, atkmax);
            // battle_add_weapon_damage overrefine term and battle_calc_sizefix are both
            // no-ops for a refine-0 Dagger against any size target (see
            // RenewalBasicAttackRules's doc comment).
        }

        long damage = statusAtk + weaponAtk; // equipAtk=0, percentAtk=0, masteryAtk=0, patk=0.

        var def1 = target.Defense;
        var def2 = (target.Level + target.Vit) / 2;
        damage = damage * (4000 + def1) / (4000 + 10L * def1) - def2;

        return damage < 1 ? new BasicAttackDamageResult(0, IsMiss: true) : new BasicAttackDamageResult((uint)damage, IsMiss: false);
    }

    // status_base_atk (status.cpp:2424) RENEWAL PC branch, str-based weapon (fists
    // and Dagger are not W_BOW/W_MUSICAL/W_WHIP/etc., so dstr uses STR not DEX).
    private static int BaseAttack(EffectiveCharacterStats attacker, ushort baseLevel)
    {
        const int pow = 0; // Fresh-character default (status.cpp/pc.cpp:9262).
        var str = (attacker.Strength * 10 + attacker.Dexterity * 10 / 5 + attacker.Luck * 10 / 3 + baseLevel * 10 / 4) / 10 + 5 * pow;
        return Math.Clamp(str, 0, ushort.MaxValue);
    }
}
