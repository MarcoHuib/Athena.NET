namespace Athena.Net.LoginServer.Db.Entities;

public sealed class LoginLogEntry
{
    public DateTime Time { get; set; }
    public string Ip { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public byte ResultCode { get; set; }
    public string Log { get; set; } = string.Empty;
}
