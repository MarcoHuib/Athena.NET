namespace Rathena.LoginServer.Db.Entities;

public sealed class AccountRegStr
{
    public uint AccountId { get; set; }
    public string Key { get; set; } = string.Empty;
    public uint Index { get; set; }
    public string Value { get; set; } = string.Empty;
}
