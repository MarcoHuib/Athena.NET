namespace Athena.Net.MapServer.World;

public sealed record GeneratedJobDefinition(ushort JobClass, string Name);
public sealed record GeneratedSkillDefinition(ushort SkillId, string Name);
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
