namespace Athena.Net.MapServer.World;

// NormallyLearnable is false for a source-backed classification of skills that are not acquired
// through ordinary skill-point spending: pinned db/re/skill_db.yml's Flags.IsQuest (e.g.
// NV_FIRSTAID, NV_TRICKDEAD - acquired via quest/event, not the skill window's normal + button),
// Flags.IsWedding (WE_* family), and Flags.IsGuild (GD_* family, granted by guild level, not
// personal skill points). True for every other skill, including one with no Flags block at all
// (e.g. NV_BASIC). This is a source-backed classification, never a hardcoded skill-name list -
// see ai/world-data.md.
public sealed record GeneratedSkillDefinition(
    ushort SkillId,
    string Name,
    IReadOnlyList<uint> SpCostByLevel,
    short Range,
    bool NormallyLearnable);
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
