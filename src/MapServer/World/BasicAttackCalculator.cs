namespace Athena.Net.MapServer.World;

// Result of one basic-attack damage calculation. Pure/deterministic: no
// mutation, no I/O. Whether the hit was lethal is answered later by
// MobInstance.ApplyDamage (which owns the authoritative HP mutation), not
// here - this type only reports what damage the attack calculates.
public readonly record struct BasicAttackDamageResult(uint Damage, bool IsMiss);

// Computes basic (unarmed, no-skill) melee damage for a Novice attacking a
// monster, following the pinned renewal (RENEWAL) formula chain EXACTLY for
// the one supported case this branch targets: a fresh character with no
// weapon equipped and no 4th-tier stat (POW) allocation, per
// legacy/rathena/src/map/status.cpp:9262 (`sd->status.pow=0` on character
// reset) - not a general renewal combat engine.
//
// Traced call chain (see report for full citations):
//  1. status_base_atk (status.cpp:2424, RENEWAL PC branch, non-bow weapon):
//     batk = floor((STR*10 + DEX*10/5 + LUK*10/3 + BaseLevel*10/4) / 10) + 5*POW.
//     POW is fixed at 0 here (see above) - this is the pinned DEFAULT for the
//     supported scenario, not an invented stat.
//  2. battle_calc_base_damage (battle.cpp:2515): for a PC with no weapon
//     equipped, atkmax=wa.atk=0 (no item contributes ATK) and atkmin=DEX is
//     then capped down to atkmax (0) because atkmin>atkmax, so the weapon-roll
//     damage term is exactly 0; damage += status->batk (battle.cpp:2624)
//     leaves base pre-DEF damage == batk exactly.
//  3. status_calc_misc (status.cpp:2600, BL_MOB branch, RENEWAL): monster soft
//     DEF (def2/"vit_def") = floor((Level+Vit)/2). mob_db.yml has no explicit
//     Vit for G_PORING, which defaults to 1 (mob.cpp:4954, spawn_data
//     constructor run before the YAML loader), NOT 0.
//  4. battle_calc_defense_reduction (battle.cpp:4720, "Mob-Pet vit-eq"
//     RENEWAL branch, target is not a PC): vit_def = def2 (no VIT-based
//     randomized roll for a mob target - that branch is PC-target only,
//     pre-RENEWAL). Then (battle.cpp:4856-4867, non-piercing, non-simple-
//     defense case): damage = damage*(4000+def1)/(4000+10*def1) - vit_def,
//     with def1 = the monster's mob_db Defense field (hard DEF).
//  5. battle_calc_attack (battle.cpp:6766): if the final damage is less than
//     1, it is a miss/absorbed result (0 damage dealt) - there is NO general
//     "minimum 1 damage" floor for a normal attack (that clamp exists only
//     for specific statuses/battlegrounds elsewhere, not this path).
//
// Deliberately NOT implemented, per task scope, and disclosed rather than
// silently approximated: the independent hit/flee accuracy roll
// (status_calc_misc:2637-2645) - this slice's only source of a "miss" is
// step 5's damage-floor rule, not a separate to-hit check; weapon size-fix,
// race/element multipliers, critical hits, skills, cards, refinement.
public static class BasicAttackCalculator
{
    public static BasicAttackDamageResult CalculateUnarmedNoviceAttack(EffectiveCharacterStats attacker, ushort attackerBaseLevel, MobDefinition target)
    {
        var baseAttack = BaseAttack(attacker, attackerBaseLevel);
        // Bare-fist weapon-roll term is 0 (see step 2 above): base damage before DEF is exactly batk.
        long damage = baseAttack;

        var def1 = target.Defense;
        var def2 = (target.Level + target.Vit) / 2; // status_calc_misc BL_MOB branch, integer floor division.

        damage = damage * (4000 + def1) / (4000 + 10L * def1) - def2;

        return damage < 1 ? new BasicAttackDamageResult(0, IsMiss: true) : new BasicAttackDamageResult((uint)damage, IsMiss: false);
    }

    // status_base_atk (status.cpp:2424) RENEWAL PC branch, str-based weapon
    // (fists are not W_BOW/W_MUSICAL/W_WHIP/etc., so DSTR uses STR not DEX).
    private static int BaseAttack(EffectiveCharacterStats attacker, ushort baseLevel)
    {
        const int pow = 0; // Fresh-character default (status.cpp pc.cpp:9262) - see type doc comment.
        var str = (attacker.Strength * 10 + attacker.Dexterity * 10 / 5 + attacker.Luck * 10 / 3 + baseLevel * 10 / 4) / 10 + 5 * pow;
        return Math.Clamp(str, 0, ushort.MaxValue);
    }
}
