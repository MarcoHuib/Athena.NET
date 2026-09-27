namespace Athena.Net.LoginServer.Startup;

public sealed class StartupOptions
{
    public string ConfigPath { get; init; } = "conf/login_athena.conf";
    public string InterConfigPath { get; init; } = "conf/inter_athena.conf";
    public string SecretsPath { get; init; } = "solutionfiles/secrets/secret.json";
    public string SubnetConfigPath { get; init; } = "conf/subnet_athena.conf";
    public string LoginMsgPath { get; init; } = "conf/msg_conf/login_msg.conf";
    public bool SelfTest { get; init; }
    public bool AutoMigrate { get; init; }

    /// <summary>
    /// One-shot account-creation mode: provisions a single player account (via
    /// IPlayerAccountProvisioningService) and exits, instead of starting the
    /// server. Used by scripts/create-player-account.sh, replacing that script's
    /// former direct-SQL insert into the legacy login table (which player login
    /// no longer reads after the Identity migration).
    /// </summary>
    public string? CreateAccountUserName { get; init; }
    public string? CreateAccountPassword { get; init; }
    public char CreateAccountSex { get; init; } = 'M';
    public string? CreateAccountEmail { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        var createAccountUserName = ArgsHelper.GetValue(args, "--create-account-username");
        var sexArg = ArgsHelper.GetValue(args, "--create-account-sex");

        return new StartupOptions
        {
            ConfigPath = ArgsHelper.GetValue(args, "--login-config") ?? "conf/login_athena.conf",
            InterConfigPath = ArgsHelper.GetValue(args, "--inter-config") ?? "conf/inter_athena.conf",
            SecretsPath = ArgsHelper.GetValue(args, "--secrets") ?? "solutionfiles/secrets/secret.json",
            SubnetConfigPath = ArgsHelper.GetValue(args, "--subnet-config") ?? "conf/subnet_athena.conf",
            LoginMsgPath = ArgsHelper.GetValue(args, "--login-msg-config") ?? "conf/msg_conf/login_msg.conf",
            SelfTest = ArgsHelper.HasFlag(args, "--self-test"),
            AutoMigrate = ArgsHelper.HasFlag(args, "--auto-migrate") ||
                string.Equals(Environment.GetEnvironmentVariable("ATHENA_NET_LOGIN_DB_AUTOMIGRATE"), "true", StringComparison.OrdinalIgnoreCase),
            CreateAccountUserName = createAccountUserName,
            CreateAccountPassword = ArgsHelper.GetValue(args, "--create-account-password"),
            CreateAccountSex = !string.IsNullOrEmpty(sexArg) ? char.ToUpperInvariant(sexArg[0]) : 'M',
            CreateAccountEmail = ArgsHelper.GetValue(args, "--create-account-email"),
        };
    }
}
