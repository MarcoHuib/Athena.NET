namespace Athena.Net.MapServer.World;

// Traced from pinned rAthena's RENEWAL_ASPD player attack-delay path (status.cpp), for the ONLY
// subset this codebase currently models: a fresh Novice with no skills/riding/shield/dual-wield
// and no active status beyond Increase AGI's flat ASPD bonus (already computed as
// EffectiveCharacterStats.AttackSpeedBonus - see CharacterStatusEffectState's own doc comment).
// Not a general ASPD/status subsystem - this is the smallest source-backed calculation that
// produces status->adelay (the real interval unit_set_attackdelay/unit_attack_timer_sub schedule
// the next repeat-attack tick with) for this exact slice.
//
// Full traced chain (status_base_amotion_pc, status.cpp:2355-2399, RENEWAL_ASPD branch, called
// from the RE PC branch of status_calc_bl_ at status.cpp:6389-6417):
//   aspd = job->aspd_base[weapon]                                   (db/re/job_aspd.yml)
//   temp_aspd = sqrt(DEX^2/5 + AGI^2*0.5) * 0.25 + 196               (non-DEX-flagged weapon type;
//                                                                     Dagger/Fist both use this
//                                                                     branch, not the Bow/etc one)
//   val = 0 (no SA_ADVANCEDBOOK/SG_DEVIL/GS_SINGLEACTION skills, no riding - all unmodeled/false
//         for a fresh Novice)
//   aspd = (int)(temp_aspd + (status_calc_aspd(fixed=true, always 0 - no active fixed-ASPD
//         status like Two-Hand Quicken/Berserk is modeled) + val) * AGI / 200) - min(aspd, 200)
// Then status_calc_bl_'s own RE PC continuation (status.cpp:6392-6417):
//   amotion = aspd (renamed, same variable reused in pinned source)
//   amotion += (max(195 - amotion, 2) * (aspd_rate2(0, unmodeled) + status_calc_aspd(fixed=false)))
//              / 100 - the non-fixed term is EXACTLY AttackSpeedBonus for this codebase's
//              modeled statuses (status.cpp:8345-8346, SC_INCREASEAGI val1, inside the `else`/
//              non-fixed branch of status_calc_aspd - the only bonus source
//              CharacterStatusEffectState.Recalculate currently produces)
//   amotion = AMOTION_ZERO_ASPD(2000) - amotion * AMOTION_INTERVAL(10)      // ASPD -> amotion
//   amotion += bAspd_add (0, no unmodeled aspd_add item bonus)
//   amotion = status_calc_fix_aspd(amotion)  // no-op: no SC_OVERED_BOOST/GUST_OPTION/etc modeled
//   status->amotion = clamp(amotion, pc_maxaspd/2, MIN_ASPD/2)     // conf/battle/player.conf
//                                                                   max_aspd=190 -> clamp(_,95,4000)
//   status->adelay = AMOTION_DIVIDER_PC(2) * status->amotion       // unit_set_attackdelay's
//                                                                   DELAY_EVENT_ATTACK input
//
// Deliberately NOT modeled (would need broader status/skill/equipment state): shields, dual-wield,
// riding, weapon-mastery/ASPD skills, DEX-flagged ranged weapon types (Bow/Musical/Whip/firearms
// use a different temp_aspd formula), any fixed-ASPD status (Two-Hand Quicken, Berserk, ASPD
// potions), aspd_rate/aspd_rate2 item or equipment-slot bonuses, bAspd_add.
public static class AttackDelayCalculator
{
    private const int AmotionZeroAspd = 2000;
    private const int AmotionInterval = 10;
    private const int AmotionDividerPc = 2;
    private const int MaxAspd = 190; // conf/battle/player.conf max_aspd
    private const int MinAspd = 8000; // status.hpp MIN_ASPD

    // Pinned db/re/job_aspd.yml Novice row. Extend only when a weapon type beyond Fist/Dagger is
    // actually exercised by this project's currently-supported gameplay slice.
    public static int BaseAspdForWeapon(WeaponType? weaponType) => weaponType switch
    {
        null => 40, // Fist (unarmed)
        WeaponType.Fist => 40,
        WeaponType.Dagger => 55,
        _ => throw new NotSupportedException($"No pinned BaseASPD is modeled for weapon type {weaponType}."),
    };

    // Returns the milliseconds between authoritative repeat-attack hits (pinned status->adelay).
    public static int AttackDelayMs(EffectiveCharacterStats attacker, WeaponType? weaponType)
    {
        var baseAspd = BaseAspdForWeapon(weaponType);
        var dex = (double)attacker.Dexterity;
        var agi = (double)attacker.Agility;
        var tempAspd = Math.Sqrt(dex * dex / 5.0 + agi * agi * 0.5) * 0.25 + 196.0;

        var fixedAspdBonus = 0; // No fixed-ASPD status (Two-Hand Quicken/Berserk/etc) is modeled.
        var aspd = (int)(tempAspd + (fixedAspdBonus + 0) * agi / 200.0) - Math.Min(baseAspd, 200);

        var amotion = aspd;
        amotion += Math.Max(195 - amotion, 2) * (0 + attacker.AttackSpeedBonus) / 100;
        amotion = AmotionZeroAspd - amotion * AmotionInterval;

        amotion = Math.Clamp(amotion, MaxAspd / AmotionDividerPc, MinAspd / AmotionDividerPc);
        return AmotionDividerPc * amotion;
    }
}
