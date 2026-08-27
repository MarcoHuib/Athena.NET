using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules.Renewal;

// RENEWAL basic (no-skill) melee damage for a monster (G_PORING-shaped: no equipped weapon
// concept, no skills) attacking a player. This is the mirror of WeaponAttackCalculator (PC
// attacker, mob target) - see that type's own doc comment for the shared formula shape; this
// file only re-derives the pieces that differ because the ATTACKER is now non-PC. Traced call
// chain:
//
//  battle.cpp:4140-4147 - RENEWAL, `if (sd) ... else { wd->damage = battle_calc_base_damage(...) }`
//  - a mob attacker always takes the `else` branch regardless of any equipped-weapon concept
//    (mobs have none), so there is exactly one pipeline here, not an armed/unarmed split.
//
//  1. battle_calc_base_damage's own `!sd` branch (battle.cpp:2526-2539): atkmin=wa->atk,
//     atkmax=wa->atk2, i.e. status->rhw.atk/atk2 - NOT the mob_db `Attack`/`Attack2` fields
//     directly. Under RENEWAL, mob_db `Attack2` is parsed into status->rhw.matk (magic attack),
//     never rhw.atk2 (mob.cpp:5107-5118) - so a mob's rhw.atk2 for a normal weapon attack instead
//     comes from status_base_atk_max/status_base_atk_min (status.cpp:2522-2554), which for
//     BL_MOB derive both bounds from the SAME single rhw.atk value (itself set directly from
//     mob_db `Attack`, mob.cpp:5098-5104): atkmin = rhw.atk*80/100, atkmax = rhw.atk*120/100.
//     damage = uniform random in [atkmin, atkmax] (battle.cpp:2571, no crit path modeled here -
//     G_PORING has no crit-relevant modeled state).
//  2. battle_calc_defense_reduction (battle.cpp:4720-4884), RENEWAL branch, target is a PC
//     (`tsd` set, battle.cpp:4806-4815): vit_def = def2 (the PLAYER's own def2, RENEWAL PC
//     formula, status.cpp:2649-2656: "stat = (int32)(((float)level + status->vit) / 2 +
//     (float)status->agi / 5)" - ONE floating-point expression truncated to int ONCE at the end,
//     NOT floor(Level+Vit)/2) as one integer division followed by a separate floor(Agi/5) - the
//     two terms share a single truncation) - directly, no extra random term (that only applies
//     pre-Renewal, battle.cpp:4808-4812 #ifndef RENEWAL). def1 is the player's hard DEF
//     (status_get_def, gear-derived) - modeled as 0 here (a fresh Novice with no DEF-granting
//     armor equipped), matching this project's other basic-attack calculators' existing
//     "disclosed fresh-character defaults" convention (see RenewalBasicAttackRules' own doc
//     comment) rather than silently approximating a nonzero value; extending this to real equipped
//     armor is a clearly separate follow-up once Athena models armor DEF at all.
//     damage = damage*(4000+def1)/(4000+10*def1) - vit_def (battle.cpp:4866-4867).
//  3. battle_calc_attack (battle.cpp:6753-6796): `if (d.damage + d.damage2 < 1) { ... if
//     (d.dmg_lv == ATK_DEF) d.dmg_lv = ATK_MISS; ... }` (battle.cpp:6766-6770) - a post-
//     defense-reduction result below 1 is reclassified from a connected hit (ATK_DEF) to a genuine
//     MISS (ATK_MISS), NOT a "successful zero-damage hit" and NOT floored to a minimum of 1 (no
//     such floor exists anywhere in this call chain for a normal weapon attack). This is answer A
//     of this project's own "miss vs. zero-damage-hit vs. minimum-1" trace requirement - confirmed
//     directly against pinned source, not assumed from the arithmetic alone.
//
//     Critically, `dmg_lv` (ATK_MISS vs ATK_DEF) is SERVER-INTERNAL bookkeeping only (gates
//     on-hit trigger effects elsewhere) - it does NOT change what clif_damage puts on the wire.
//     wd.div_ is `skill_id ? skill_get_num(...) : 1` (battle.cpp:5286), so a plain basic attack
//     (skill_id=0) always sends div=1 whether it hits or misses; wd.type stays DMG_NORMAL for an
//     ordinary attack either way (battle.cpp:7399's own clif_damage(..., wd.div_, wd.type, ...)
//     call passes exactly these two fields, both hit and miss). The ONLY wire-visible difference
//     between a miss and a real hit is damage=0 itself (plus the resulting client-side omission of
//     the target's own hit-flinch/HP-bar movement) - there is no separate "miss" flag/type value to
//     set. MapClientSession.NotifyMonsterAttackOutcomeAsync's own `div: 1, actionType: 0`
//     (DMG_NORMAL) already matches this exactly for both the hit and miss cases; srcSpeed/dstSpeed
//     (the attacker's own AttackMotion/the target's DamageMotion) are sent unconditionally so the
//     attack swing animation itself always plays, miss or not - a real miss on live stock iRO
//     LOOKS like the mob swinging and missing, not like nothing happening at all.
//
// Deliberately NOT implemented, same scope boundary as RenewalBasicAttackRules: hit/FLEE accuracy
// roll, elemental attribute table, cards, size/racial modifiers, player armor DEF (see step 2),
// skills, and any status-effect-driven damage modifier.
internal static class MobBasicAttackCalculator
{
    public static BasicAttackDamageResult Calculate(MobDefinition attacker, PlayerCombatSnapshot target, Func<int, int, int>? rollAtk = null)
    {
        rollAtk ??= (min, max) => min >= max ? min : Random.Shared.Next(min, max + 1);

        var rhwAtk = attacker.Attack;
        var atkmin = rhwAtk * 80 / 100;
        var atkmax = rhwAtk * 120 / 100;
        if (atkmin > atkmax) atkmin = atkmax;

        long damage = rollAtk(atkmin, atkmax);

        const int def1 = 0; // Disclosed simplification - see this type's own doc comment, step 2.
        var def2 = (int)((float)(target.BaseLevel + target.Vitality) / 2 + (float)target.Agility / 5);
        damage = damage * (4000 + def1) / (4000 + 10L * def1) - def2;

        return damage < 1 ? new BasicAttackDamageResult(0, IsMiss: true) : new BasicAttackDamageResult((uint)damage, IsMiss: false);
    }
}
