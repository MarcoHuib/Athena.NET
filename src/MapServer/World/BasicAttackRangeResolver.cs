namespace Athena.Net.MapServer.World;

// Traced from pinned rAthena's status_get_range (status.hpp:3582: "#define status_get_range(bl)
// status_get_status_data(*bl)->rhw.range") for the ONLY subset this codebase currently models: a
// PC's basic-melee/unarmed attack range, with no active statuses/skills/cards that would modify
// it. Not a general status-recalculation subsystem - this is the smallest source-backed
// calculation that produces status->rhw.range (the value unit_attack/unit_attack_timer_sub read
// as `range` before deciding whether an attack is even possible - unit.cpp:2946,3251).
//
// Full traced chain for a PC's rhw.range (status_calc_pc_, status.cpp):
//   1. The equipped right-hand weapon's OWN item_db Range column seeds rhw.range (this project's
//      WeaponItemDefinition.Range, read generically from every Type: Weapon row - see
//      ItemDataCompiler.ReadItemDefinition). Unarmed (no right-hand weapon) has no item-db row at
//      all; pinned source's own equipment-parse loop simply never writes a nonzero range for an
//      empty slot, so this resolver treats "no equipped weapon" as pre-clamp range 0, identically
//      to a Range:0 weapon at this stage.
//   2. status_calc_pc_'s own unconditional floor, applied AFTER every equipment/bonus pass
//      (status.cpp:4216-4219):
//          if (base_status->rhw.range < 1) base_status->rhw.range = 1;
//          if (base_status->lhw.range < 1) base_status->lhw.range = 1;
//          if (base_status->rhw.range < base_status->lhw.range) base_status->rhw.range = base_status->lhw.range;
//      This project has no left-hand/dual-wield weapon modeled (lhw.range is always the pinned
//      default 0, floored to 1 same as rhw would be) - the lhw comparison is therefore always a
//      no-op here (rhw's own floor-to-1 already dominates), so only the rhw floor is reproduced.
//   3. unit_attack_timer_sub's own +1 "chasing" bonus (unit.cpp:3251-3253): "if (unit_is_walking
//      (target) || step_attack) && (PC target || target not in icewall) -> range++" - applied by
//      the CALLER (BasicAttackDistanceValidator), not here, since it depends on the TARGET's
//      current movement state at the moment of a specific attack attempt, not on the attacker's
//      own equipment/status - this resolver only ever returns the equipment-derived base range.
//
// Deliberately NOT modeled (would need broader status/skill/card/equipment state this project
// does not yet represent): bAtkRange script bonuses (SP_ATTACKRANGE, pc.cpp:3942-3960 - bow/
// firearm ammo-range bonuses and left-hand weapon range bonuses), any card/skill/status range
// modifier, dual-wield/left-hand weapons, homunculus/mercenary/pet range. These are real pinned
// semantics Athena cannot yet represent - documented here as future extensions, never guessed at.
public static class BasicAttackRangeResolver
{
    // The single, authoritative way to turn "this character's CURRENT equipped weapon" (already
    // resolved via EquippedWeaponResolver - never inferred from ClientViewId/WeaponType) into the
    // pinned status_get_range value a distance check must use. `equippedWeapon` is null for a
    // confirmed-unarmed right hand (EquippedWeaponResolution.Unarmed) - never for an unresolved/
    // invalid equipment state, which callers must reject before reaching here (see
    // PerformDueRepeatAttackAsync's own UnknownItem/NonWeaponInWeaponSlot handling).
    public static int Resolve(WeaponItemDefinition? equippedWeapon)
    {
        var rawRange = equippedWeapon?.Range ?? 0;
        return Math.Max(1, rawRange); // status.cpp:4216's unconditional floor-to-1.
    }
}
