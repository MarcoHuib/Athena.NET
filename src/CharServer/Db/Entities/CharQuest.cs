namespace Athena.Net.CharServer.Db.Entities;

public sealed class CharQuest
{
    public uint CharId { get; set; }
    public uint QuestId { get; set; }
    public string State { get; set; } = "0";
    public uint Time { get; set; }
    public uint Count1 { get; set; }
    public uint Count2 { get; set; }
    public uint Count3 { get; set; }
}
