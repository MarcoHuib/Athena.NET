namespace Rathena.LoginServer.Db;

public sealed class LoginDbTableNames
{
    public static readonly LoginDbTableNames Default = new();

    public string AccountTable { get; init; } = "login";
    public string IpBanTable { get; init; } = "ipbanlist";
    public string LoginLogTable { get; init; } = "loginlog";
    public string GlobalAccRegNumTable { get; init; } = "global_acc_reg_num";
    public string GlobalAccRegStrTable { get; init; } = "global_acc_reg_str";
}
