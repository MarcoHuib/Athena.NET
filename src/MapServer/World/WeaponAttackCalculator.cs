namespace Athena.Net.MapServer.World;

// Computes RENEWAL basic (no-skill) melee damage for a Novice PC attacking a monster
// with a real equipped right-hand weapon (e.g. the starter Knife), following the
// PINNED SOURCE PATH ACTUALLY USED FOR A PC IN RENEWAL - NOT the generic
// battle_calc_base_damage BasicAttackCalculator already implements for the unarmed
// case (that function's PC branch is only reached for magic/BDMG_MAGIC and is not
// what a real client-observed PC normal attack executes). The real path is
// battle_calc_damage_parts (battle.cpp:3889) + battle_calc_defense_reduction
// (battle.cpp:4720), traced field-by-field below for the one case this slice
// supports: a fresh Novice, no active statuses/cards/skills, right-hand weapon only.
//
// Traced call chain:
//  1. status_base_atk (status.cpp:2424, RENEWAL PC branch, non-DEX-flagged weapon:
//     Dagger is not W_BOW/W_MUSICAL/W_WHIP/W_REVOLVER/W_RIFLE/W_GATLING/W_SHOTGUN/
//     W_GRENADE, so dstr=str, not status->dex):
//     batk = floor((STR*10 + DEX*10/5 + LUK*10/3 + BaseLevel*10/4) / 10) + 5*POW.
//     POW fixed at 0 (fresh-character default, matching BasicAttackCalculator's own
//     documented precedent).
//  2. battle_calc_damage_parts (battle.cpp:3889-3952):
//       statusAtk = battle_attr_fix(batk, neutral element, ...) = batk (no-op, no
//         elemental target here), then DOUBLED: statusAtk *= 2 (battle.cpp:3911).
//       weaponAtk = battle_calc_base_weapon_attack(...) (battle.cpp:3917), see below.
//       equipAtk = battle_calc_equip_attack (battle.cpp:3934) = status->eatk (0 - no
//         eatk-granting cards/items modeled) + arrow bonus (0 - not an arrow attack).
//       masteryAtk = battle_addmastery(...) (battle.cpp:3801) - every bonus branch in
//         battle_addmastery (battle.cpp:2277) is gated on a weapon-mastery/racial
//         skill level > 0; a fresh Novice has none, so this is exactly 0, not
//         approximated as 0.
//  3. battle_calc_base_weapon_attack (battle.cpp:2443), RENEWAL PC weapon-roll term:
//       atkmin = atkmax = status->watk = status_calc_watk(rhw.atk) = rhw.atk with no
//         active SC_* watk modifiers (status.cpp:7339-7341 early-out) = the equipped
//         weapon's own item_db Attack field (WeaponItemDefinition.Attack) at refine 0
//         (status.cpp:3955-3969: wa->atk += item.atk; the refine_db bonus term is
//         entirely gated behind a non-null refine_level_info, which does not exist
//         for refine 0 - "no refine bonus" is the correct behavior for refine 0, not
//         an approximation).
//       variance = 5.0 * wa.atk * wa.wlv / 100 (wa.wlv = WeaponItemDefinition.WeaponLevel).
//       base_stat_bonus = wa.atk * STR / 200 (base_stat is STR for every non-DEX-
//         flagged weapon type per the same switch as step 1 - Dagger included).
//       atkmin = max(0, floor(watk - variance + base_stat_bonus)).
//       atkmax = min(65535, floor(watk + variance + base_stat_bonus)).
//       damage = rnd_value(atkmin, atkmax) inclusive (no SC_MAXIMIZEPOWER/critical
//         modeled - out of scope, matching BasicAttackCalculator's own precedent of
//         not modeling crits).
//       battle_add_weapon_damage (battle.cpp:2407): adds rnd()%overrefine+1 only if
//         right_weapon.overrefine != 0; refine 0 -> overrefine 0 -> no-op.
//       battle_calc_sizefix (battle.cpp:2427): damage * atkmods[target size] / 100.
//         Dagger has NO entry in the pinned size_fix.yml (only Knuckle/Whip carry
//         Large-only entries there); the database's own documented column default
//         for every unlisted weapon/size pair is 100 (db/re/size_fix.yml header:
//         "Small/Medium/Large ... Default: 100"), which is a genuine no-op multiplier
//         - this is NOT a capture-matched constant, it is the pinned data file's own
//         stated default for the one weapon type this slice targets.
//  4. wd.damage = statusAtk + weaponAtk + equipAtk(0) + percentAtk(0) (battle.cpp:5525).
//     P.ATK mod: floor(damage*(100+patk)/100); patk is POW-derived and 0 for a fresh
//     character (battle.cpp:5532) - no-op.
//     wd.damage += masteryAtk(0) (battle.cpp:5538) - no-op per step 2.
//  5. battle_calc_defense_reduction (battle.cpp:4720): target is not a PC, so
//     vit_def = def2 = floor((Level+Vit)/2) directly (battle.cpp:4834-4835, RENEWAL
//     "SoftDEF of monsters" comment) - IDENTICAL soft-DEF rule to
//     BasicAttackCalculator's existing def2 computation, reused unchanged here.
//     damage = damage*(4000+def1)/(4000+10*def1) - vit_def (battle.cpp:4867), where
//     def1 = target.Defense (mob_db hard DEF) - the SAME RE DEF-reduction formula
//     BasicAttackCalculator already implements, reused unchanged.
//  6. battle_calc_attack (battle.cpp:6766): damage < 1 is a miss/absorbed result (0
//     damage), matching BasicAttackCalculator's existing miss rule exactly - there is
//     still no separate minimum-1-damage floor for a normal attack.
//
// Deliberately NOT implemented, per task scope and disclosed rather than silently
// approximated: hit/FLEE accuracy roll, elemental attribute table, cards, refine > 0,
// left-hand/dual-wield, critical hits, skills, ammo/arrow attacks, weapon mastery
// skills (correctly 0 for a fresh Novice, not "always 0"), P.ATK/POW (correctly 0 for
// a fresh Novice), and any non-Dagger weapon_type's size_fix.yml row.
public static class WeaponAttackCalculator
{
    // `rollWeaponAtk` abstracts rAthena's `rnd_value(atkmin, atkmax)` (inclusive) so
    // tests can pin the weapon-ATK roll deterministically; production passes the
    // real inclusive-random default.
    public static BasicAttackDamageResult CalculateWeaponNoviceAttack(
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition weapon,
        MobDefinition target,
        Func<int, int, int>? rollWeaponAtk = null)
    {
        rollWeaponAtk ??= (min, max) => min >= max ? min : Random.Shared.Next(min, max + 1);

        var batk = BaseAttack(attacker, attackerBaseLevel);
        var statusAtk = 2L * batk;

        var watk = weapon.Attack; // status_calc_watk no-op: no active watk-modifying status.
        var variance = 5.0 * watk * weapon.WeaponLevel / 100.0;
        var baseStatBonus = watk * attacker.Strength / 200.0;
        var atkmin = Math.Max(0, (int)(watk - variance + baseStatBonus));
        var atkmax = Math.Min(ushort.MaxValue, (int)(watk + variance + baseStatBonus));
        if (atkmin > atkmax) atkmin = atkmax; // Defensive: pinned code never violates this for real data.

        long weaponAtk = rollWeaponAtk(atkmin, atkmax);
        // battle_add_weapon_damage overrefine term and battle_calc_sizefix are both
        // no-ops for a refine-0 Dagger against any size target (see doc comment).

        long damage = statusAtk + weaponAtk; // equipAtk=0, percentAtk=0, masteryAtk=0, patk=0.

        var def1 = target.Defense;
        var def2 = (target.Level + target.Vit) / 2;
        damage = damage * (4000 + def1) / (4000 + 10L * def1) - def2;

        return damage < 1 ? new BasicAttackDamageResult(0, IsMiss: true) : new BasicAttackDamageResult((uint)damage, IsMiss: false);
    }

    // Identical to BasicAttackCalculator's own BaseAttack (status_base_atk, non-DEX-
    // flagged weapon branch) - Dagger uses the same STR-based dstr as bare fists.
    private static int BaseAttack(EffectiveCharacterStats attacker, ushort baseLevel)
    {
        const int pow = 0;
        var str = (attacker.Strength * 10 + attacker.Dexterity * 10 / 5 + attacker.Luck * 10 / 3 + baseLevel * 10 / 4) / 10 + 5 * pow;
        return Math.Clamp(str, 0, ushort.MaxValue);
    }
}
