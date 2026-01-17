namespace Athena.Net.LoginServer.Config;

public sealed class InterConfig
{
    public string LoginDbProvider { get; init; } = string.Empty;
    public string LoginDbConnectionString { get; init; } = string.Empty;
    public string LoginDbCodepage { get; init; } = string.Empty;
    public bool LoginCaseSensitive { get; init; }
    public string LoginAccountTable { get; init; } = "login";
    public string IpBanTable { get; init; } = "ipbanlist";
    public string LoginLogTable { get; init; } = "loginlog";
    public string GlobalAccRegNumTable { get; init; } = "global_acc_reg_num";
    public string GlobalAccRegStrTable { get; init; } = "global_acc_reg_str";
}
