namespace Athena.Net.LoginServer.Db.Entities;

public sealed class AccountRegNum
{
    public uint AccountId { get; set; }
    public string Key { get; set; } = string.Empty;
    public uint Index { get; set; }
    public long Value { get; set; }
}
