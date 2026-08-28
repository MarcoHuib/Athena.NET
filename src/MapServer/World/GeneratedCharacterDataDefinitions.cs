namespace Athena.Net.MapServer.World;

// Pinned db/re/skill_db.yml's Flags block markers relevant to pc_calc_skilltree/pc_check_skilltree's
// player skill-tree gate (pc.cpp:2735-2740/2862-2867 - traced against pinned e985006... in
// ai/iro-2026-wire.md). These are SOURCE FACTS only - CharacterSkillService, not this record,
// decides the actual current learnability/visibility policy from these facts plus server
// config/character state (see CharacterSkillState's own doc comment). IsGuild is deliberately NOT
// modeled here: it is not referenced anywhere in the player skill-tree tree-walk gate (confirmed
// against pinned pc_calc_skilltree/pc_check_skilltree) - it is a wholly separate guild-skill code
// path that never populates sd->status.skill[].
public sealed record SkillAcquisitionFlags(bool IsQuest, bool IsWedding, bool IsSpirit)
{
    public static readonly SkillAcquisitionFlags None = new(false, false, false);
}

// Pinned db/re/skill_db.yml Flags entries that alter skill_get_range2's resolved range for a
// player caster (skill.cpp:324-365 - see IroSkillRangeResolver). Each is a source fact only; the
// resolver decides HOW it applies (which companion skill's learned level to add, or which
// companion skill's own range to substitute) - never a per-skill-name special case at the call
// site. Multiple flags on the same skill are additive in pinned source (Vulture/SnakeEye can both
// apply; ShadowJump/Radius/ResearchTrap can all apply in the same second `if` block) - this record
// preserves that shape exactly rather than collapsing to "the one flag that applies".
public sealed record SkillRangeFlags(
    bool AlterRangeVulture,
    bool AlterRangeSnakeEye,
    bool AlterRangeShadowJump,
    bool AlterRangeRadius,
    bool AlterRangeResearchTrap)
{
    public static readonly SkillRangeFlags None = new(false, false, false, false, false);
}

public sealed record GeneratedSkillDefinition(
    ushort SkillId,
    string Name,
    IReadOnlyList<uint> SpCostByLevel,
    // Pinned db/re/skill_db.yml's Range is genuinely level-dependent, exactly like Requires.SpCost:
    // either a bare scalar (applies at every level) or an explicit per-level "- Level: N / Size: X"
    // sequence (e.g. KN_SPEARBOOMERANG has 5 distinct per-level ranges). A skill with no Range
    // field at all (e.g. NV_BASIC) has an empty RangeByLevel, matching pinned source's zero-fill
    // default for the absent field (skill.cpp: memset(skill->range, 0, ...)). See ai/world-data.md.
    IReadOnlyList<short> RangeByLevel,
    SkillAcquisitionFlags Acquisition,
    // Pinned SKILLDATA.inf (clif_skillinfoblock, clif.cpp:5714): sourced from skill_db.yml's
    // TargetType field via skill_get_inf, an e_skill_inf bitmask (0=passive/INF_PASSIVE_SKILL,
    // 1=INF_ATTACK_SKILL, 2=INF_GROUND_SKILL, 4=INF_SELF_SKILL, 16=INF_SUPPORT_SKILL,
    // 32=INF_TRAP_SKILL). Absent TargetType defaults to 0 (passive), matching pinned source's
    // zero-initialized struct default - never a hardcoded per-skill lookup.
    ushort Inf,
    SkillRangeFlags RangeFlags);
public sealed record SkillPrerequisite(ushort SkillId, ushort Level);
public sealed record GeneratedSkillTreeEntry(
    ushort SkillId,
    ushort MaxLevel,
    ushort BaseLevel,
    ushort JobLevel,
    IReadOnlyList<SkillPrerequisite> Prerequisites,
    bool ExcludeFromInheritance);
public sealed record GeneratedSkillTreeDefinition(
    ushort JobClass,
    IReadOnlyList<ushort> InheritedFrom,
    IReadOnlyList<GeneratedSkillTreeEntry> DeclaredSkills,
    IReadOnlyList<GeneratedSkillTreeEntry> EffectiveSkills);
