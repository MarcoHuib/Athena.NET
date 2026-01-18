namespace Athena.Net.CharServer.Db;

public sealed class CharDbTableNames
{
    public string CharTable { get; init; } = "char";
    public string InventoryTable { get; init; } = "inventory";
    public string SkillTable { get; init; } = "skill";
    public string HotkeyTable { get; init; } = "hotkey";
}
