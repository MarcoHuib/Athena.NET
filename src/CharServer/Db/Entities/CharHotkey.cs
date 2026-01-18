namespace Athena.Net.CharServer.Db.Entities;

public sealed class CharHotkey
{
    public uint CharId { get; set; }
    public byte Hotkey { get; set; }
    public byte Type { get; set; }
    public uint ItemSkillId { get; set; }
    public byte SkillLevel { get; set; }
}
