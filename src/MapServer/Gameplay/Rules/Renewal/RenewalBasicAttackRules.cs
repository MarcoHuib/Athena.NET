namespace Athena.Net.MapServer.Gameplay.Rules.Renewal;

// RENEWAL basic (no-skill) melee damage for a Novice PC attacking a monster, with an
// OPTIONAL equipped right-hand weapon (e.g. the starter Knife) - armed and unarmed
// attacks share exactly one pinned-source pipeline, not two. Traced call chain:
//
//  battle.cpp:4140-4142 - `if (sd) battle_calc_damage_parts(...) else
//  battle_calc_base_damage(...)`. The branch is gated purely on "is this attacker a
//  PC" (sd), NEVER on whether a weapon is equipped. An unarmed PC normal attack
//  therefore goes through the SAME battle_calc_damage_parts pipeline as an armed
//  one - battle_calc_base_damage (the function an earlier, now-removed
//  BasicAttackCalculator incorrectly used for the unarmed case) is exclusively the
//  non-PC/monster branch and is never reached by any PC normal attack in RENEWAL.
//
//  1. status_base_atk (status.cpp:2424, RENEWAL PC branch, non-DEX-flagged weapon:
//     Dagger/fists are not W_BOW/W_MUSICAL/W_WHIP/W_REVOLVER/W_RIFLE/W_GATLING/
//     W_SHOTGUN/W_GRENADE, so dstr=str, not status->dex):
//     batk = floor((STR*10 + DEX*10/5 + LUK*10/3 + BaseLevel*10/4) / 10) + 5*POW.
//     POW fixed at 0 (fresh-character default, status.cpp/pc.cpp:9262).
//  2. battle_calc_damage_parts (battle.cpp:3889-3952):
//       statusAtk = battle_attr_fix(batk, neutral element, ...) = batk (no-op, no
//         elemental target here), then DOUBLED: statusAtk *= 2 (battle.cpp:3911).
//         This doubling applies unconditionally - unarmed included.
//       weaponAtk = battle_calc_base_weapon_attack(...) (battle.cpp:3917) - see
//         WeaponAttackCalculator. For a confirmed-unarmed right hand, its own
//         `if (sd && sd->equip_index[type] >= 0 ...)` guard (battle.cpp:2453) is
//         false, leaving atkmin=atkmax=status->watk=0 (an unarmed PC's rhw.atk is
//         never populated by the equipment-parse loop, status.cpp:3940-3955), so
//         weaponAtk collapses to exactly 0 - not approximated as 0, traced as 0.
//       equipAtk = battle_calc_equip_attack (battle.cpp:3934) = status->eatk (0 - no
//         eatk-granting cards/items modeled) + arrow bonus (0 - not an arrow attack).
//       masteryAtk = battle_addmastery(...) (battle.cpp:3801) - every bonus branch in
//         battle_addmastery (battle.cpp:2277) is gated on a weapon-mastery/racial
//         skill level > 0; a fresh Novice has none, so this is exactly 0.
//  3. battle_calc_base_weapon_attack (battle.cpp:2443), RENEWAL PC weapon-roll term
//     (armed case only - see WeaponAttackCalculator for the full sub-trace):
//     atkmin/atkmax from item Attack (refine 0 = no bonus) +/- variance +
//     STR-based base_stat_bonus, then rnd_value(atkmin,atkmax) inclusive,
//     battle_add_weapon_damage (overrefine, no-op at refine 0), battle_calc_sizefix
//     (Dagger has no db/re/size_fix.yml row; its own documented column default is
//     100 for every unlisted weapon/size pair - a genuine no-op, not a
//     capture-matched constant).
//  4. wd.damage = statusAtk + weaponAtk + equipAtk(0) + percentAtk(0) (battle.cpp:5525).
//     P.ATK mod: floor(damage*(100+patk)/100); patk is POW-derived, 0 for a fresh
//     character (battle.cpp:5532) - no-op. wd.damage += masteryAtk(0) (battle.cpp:5538).
//  5. battle_calc_defense_reduction (battle.cpp:4720): target is not a PC, so
//     vit_def = def2 = floor((Level+Vit)/2) directly (battle.cpp:4834-4835, RENEWAL
//     "SoftDEF of monsters" comment). damage = damage*(4000+def1)/(4000+10*def1) -
//     vit_def (battle.cpp:4867), def1 = target.Defense (mob_db hard DEF).
//  6. battle_calc_attack (battle.cpp:6766): damage < 1 is a miss/absorbed result (0
//     damage) - there is no separate minimum-1-damage floor for a normal attack.
//
// Deliberately NOT implemented, per task scope and disclosed rather than silently
// approximated: hit/FLEE accuracy roll, elemental attribute table, cards, refine > 0,
// left-hand/dual-wield, critical hits, skills, ammo/arrow attacks, weapon mastery
// skills (correctly 0 for a fresh Novice, not "always 0"), P.ATK/POW (correctly 0
// for a fresh Novice), and any non-Dagger weapon_type's size_fix.yml row.
//
// This class owns RENEWAL-specific basic-attack gameplay mechanics only: it does not
// mutate monster HP, award drops, or parse packets - MonsterCombatCoordinator owns
// the attack/damage/death/drop state transition and calls this through
// IBasicAttackRules without knowing which ruleset is active.
public sealed class RenewalBasicAttackRules(Func<int, int, int>? rollWeaponAtk = null) : IBasicAttackRules
{
    public BasicAttackDamageResult Calculate(BasicAttackContext context) =>
        WeaponAttackCalculator.Calculate(context.Attacker, context.AttackerBaseLevel, context.EquippedWeapon, context.Target, rollWeaponAtk);
}
