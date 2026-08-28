using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Resolves GeneratedSkillDefinition.Range (raw pinned skill_db.yml source data) into the actual
// value 0x0B32 should display, per pinned skill_get_range2 (legacy/rathena/src/map/skill.cpp:
// 324-365). This is a SEPARATE pure resolution boundary from the byte serializer
// (IroSkillInfoListPackets.Build stays pure over already-resolved values, per task section 22) and
// from generated data (GeneratedSkillDefinition.Range is source data, not necessarily final wire
// range - see ai/world-data.md).
//
// Implemented (traced against pinned source, feasible with Athena's current state):
//   - A non-negative Range passes through unchanged.
//   - A negative Range is absolute-valued by default (skill.cpp:333: `range *= -1;`) - this is the
//     pinned DEFAULT behavior. The alternative branch (`skillrange_from_weapon` /
//     battle_config.use_weapon_skill_range, default BL_NUL = disabled for every bl type in pinned
//     conf/battle/battle.conf) would substitute the caster's live weapon/status range instead -
//     Athena has no server-config system yet, so this always takes the pinned DEFAULT (disabled)
//     path, matching an unmodified stock server.
//
// Explicitly NOT implemented (documented gap, never silently approximated): the Vulture's Eye
// (AC_VULTURE)/Snake Eye (GS_SNAKEEYE)/Shadow Jump (NJ_SHADOWJUMP)/Radius (WL_RADIUS)/Research Trap
// (RA_RESEARCHTRAP) range bonuses, which are keyed off a per-skill `inf2` ALTERRANGE* bitset this
// project does not generate and the caster's own OTHER learned skill levels. A skill flagged with
// one of those inf2 bits does not exist in the currently-generated Novice/early-game trees this
// slice targets; if one is ever added to a supported job's effective tree, this resolver's result
// for that skill is an explicitly narrowed/incomplete value, not a silently wrong one.
internal static class IroSkillRangeResolver
{
    internal static short Resolve(GeneratedSkillDefinition canonical)
    {
        return canonical.Range < 0 ? (short)-canonical.Range : canonical.Range;
    }
}
