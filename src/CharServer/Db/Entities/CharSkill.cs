namespace Athena.Net.CharServer.Db.Entities;

public sealed class CharSkill
{
    public uint CharId { get; set; }
    public ushort SkillId { get; set; }
    public byte SkillLevel { get; set; }
    public byte Flag { get; set; }
}
