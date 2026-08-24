using Athena.Net.CharServer.Db.Entities;

namespace Athena.Net.CharServer.Net;

internal sealed record CharacterGameplayStateDto(uint CharacterId, ulong Version, ushort JobClass, ushort BaseLevel, ushort JobLevel,
    ulong BaseExperience, ulong JobExperience, uint CurrentHp, uint CurrentSp, uint MaxHp, uint MaxSp,
    uint StatPoints, uint SkillPoints, ushort Strength, ushort Agility, ushort Vitality, ushort Intelligence,
    ushort Dexterity, ushort Luck)
{
    internal static CharacterGameplayStateDto From(CharCharacter value) => new(value.CharId, value.GameplayStateVersion, value.Class,
        value.BaseLevel, value.JobLevel, value.BaseExp, value.JobExp, value.Hp, value.Sp, value.MaxHp, value.MaxSp,
        value.StatusPoint, value.SkillPoint, value.Str, value.Agi, value.Vit, value.Int, value.Dex, value.Luk);
}
