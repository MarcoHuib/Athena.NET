namespace Athena.Net.CharServer.Startup;

public sealed class StartupOptions
{
    public string ConfigPath { get; init; } = "conf/char_athena.conf";
    public string InterConfigPath { get; init; } = "conf/inter_athena.conf";
    public string SecretsPath { get; init; } = "solutionfiles/secrets/secret.json";
    public bool AutoMigrate { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        return new StartupOptions
        {
            ConfigPath = ArgsHelper.GetValue(args, "--char-config") ?? "conf/char_athena.conf",
            InterConfigPath = ArgsHelper.GetValue(args, "--inter-config") ?? "conf/inter_athena.conf",
            SecretsPath = ArgsHelper.GetValue(args, "--secrets") ?? "solutionfiles/secrets/secret.json",
            AutoMigrate = ArgsHelper.HasFlag(args, "--auto-migrate") ||
                string.Equals(Environment.GetEnvironmentVariable("ATHENA_NET_CHAR_DB_AUTOMIGRATE"), "true", StringComparison.OrdinalIgnoreCase),
        };
    }
}
