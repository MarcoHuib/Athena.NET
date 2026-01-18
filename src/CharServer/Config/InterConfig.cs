namespace Athena.Net.CharServer.Config;

public sealed class InterConfig
{
    public string CharDbProvider { get; init; } = string.Empty;
    public string CharDbConnectionString { get; init; } = string.Empty;
    public string CharTable { get; init; } = "char";
    public string InventoryTable { get; init; } = "inventory";
    public string SkillTable { get; init; } = "skill";
    public string HotkeyTable { get; init; } = "hotkey";
    public int StartStatusPoints { get; init; } = 48;
}
