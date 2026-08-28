using Athena.Net.MapServer.Generated.Skills;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Resolves GeneratedSkillDefinition's per-level RangeByLevel (raw pinned skill_db.yml source data)
// into the actual value 0x0B32 should display, per pinned skill_get_range2
// (legacy/rathena/src/map/skill.cpp:324-365). This is a SEPARATE pure resolution boundary from the
// byte serializer (IroSkillInfoListPackets.Build stays pure over already-resolved values, per task
// section 22) and from generated data (RangeByLevel is source data, not necessarily final wire
// range - see ai/world-data.md).
//
// Uses the character's CURRENT level (matching SpCost's own current-level selection, per
// clif_skillinfoblock, clif.cpp:5738: skill_get_range2(&sd, skill.id, skill.lv, false)) - never a
// job-independent scalar resolved before knowing CurrentLevel.
internal static class IroSkillRangeResolver
{
    // Companion skills' own canonical SkillIds - these are the exact same identities the source
    // flags reference (skill.cpp:336-354: pc_checkskill(bl, AC_VULTURE) etc.), not a per-skill
    // runtime special case. The resolver logic below is entirely driven by
    // GeneratedSkillDefinition.RangeFlags; these constants are only WHICH companion skill's level
    // to look up once a flag says to.
    private const ushort AcVulture = 44;
    private const ushort GsSnakeEye = 510;
    private const ushort NjShadowJump = 529;
    private const ushort WlRadius = 2208;
    private const ushort RaResearchTrap = 2248;

    // Pinned skill.cpp:349: `int rt_range[11] = { 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5 };` indexed by
    // the caster's own RA_RESEARCHTRAP level (0-10).
    private static readonly int[] ResearchTrapRangeByLevel = [0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5];

    internal static short Resolve(GeneratedSkillDefinition skill, ushort currentLevel, CharacterSkillSnapshot learnedSkills)
    {
        int rawRange = currentLevel > 0 && currentLevel <= skill.RangeByLevel.Count
            ? skill.RangeByLevel[currentLevel - 1]
            : 0;

        // Pinned skill.cpp:330-334: a negative range is absolute-valued by default. The
        // alternative branch (`skillrange_from_weapon`/battle_config.use_weapon_skill_range,
        // pinned default BL_NUL = disabled for every actor type) would substitute the caster's
        // live weapon/status range instead - Athena has no server-config system yet, so this
        // always takes the pinned DEFAULT (disabled) path, matching an unmodified stock server.
        // Kept as a plain int internally (matching pinned skill_get_range2's own int range
        // arithmetic) - only the final return value is narrowed to short.
        var range = rawRange < 0 ? -rawRange : rawRange;

        // Pinned skill.cpp:336-345: Vulture's Eye / Snake Eye ADD the caster's own learned level
        // of the corresponding companion skill. Both may apply to the same skill (pinned source
        // checks each flag independently, additive) - never an if/else.
        var flags = skill.RangeFlags;
        if (flags.AlterRangeVulture) range += learnedSkills.CurrentLevel(AcVulture);
        if (flags.AlterRangeSnakeEye) range += learnedSkills.CurrentLevel(GsSnakeEye);

        // Pinned skill.cpp:346-354: Shadow Jump REPLACES the range outright with NJ_SHADOWJUMP's
        // own range at the caster's learned NJ_SHADOWJUMP level (skill_get_range, not
        // skill_get_range2 - no further modifiers/absolute-value applied to this replacement
        // value); Radius/Research Trap ADD the caster's own learned level (Research Trap through
        // the pinned non-linear rt_range table). All three are evaluated independently and can
        // combine on the same skill, matching pinned source's three separate `if` statements.
        if (flags.AlterRangeShadowJump)
        {
            var shadowJumpLevel = learnedSkills.CurrentLevel(NjShadowJump);
            var shadowJump = GeneratedSkillRegistry.GetById(NjShadowJump);
            range = shadowJumpLevel > 0 && shadowJumpLevel <= shadowJump.RangeByLevel.Count
                ? shadowJump.RangeByLevel[shadowJumpLevel - 1]
                : 0;
        }
        if (flags.AlterRangeRadius) range += learnedSkills.CurrentLevel(WlRadius);
        if (flags.AlterRangeResearchTrap)
        {
            var researchTrapLevel = Math.Min(learnedSkills.CurrentLevel(RaResearchTrap), (byte)(ResearchTrapRangeByLevel.Length - 1));
            range += ResearchTrapRangeByLevel[researchTrapLevel];
        }

        return (short)range;
    }
}
