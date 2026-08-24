namespace Athena.Net.MapServer.World;

public sealed record CharacterGameplayState(
    uint CharacterId,
    ulong Version,
    ushort BaseLevel,
    ushort JobLevel,
    ulong BaseExperience,
    ulong JobExperience,
    uint CurrentHp,
    uint CurrentSp,
    uint MaxHp,
    uint MaxSp,
    uint StatPoints,
    uint SkillPoints,
    ushort Strength,
    ushort Agility,
    ushort Vitality,
    ushort Intelligence,
    ushort Dexterity,
    ushort Luck);
