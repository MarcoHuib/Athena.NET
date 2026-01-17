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

    public static StartupOptions Parse(string[] args)
    {
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
        };
    }
}
