namespace Athena.Net.LoginServer.Db.Entities;

public sealed class IpBanEntry
{
    public string List { get; set; } = string.Empty;
    public DateTime BanTime { get; set; }
    public DateTime ReleaseTime { get; set; }
    public string Reason { get; set; } = string.Empty;
}
