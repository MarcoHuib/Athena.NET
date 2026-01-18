namespace Athena.Net.MapServer.Startup;

public sealed class StartupOptions
{
    public string ConfigPath { get; init; } = "conf/map_athena.conf";
    public string SecretsPath { get; init; } = "solutionfiles/secrets/secret.json";

    public static StartupOptions Parse(string[] args)
    {
        return new StartupOptions
        {
            ConfigPath = ArgsHelper.GetValue(args, "--map-config") ?? "conf/map_athena.conf",
            SecretsPath = ArgsHelper.GetValue(args, "--secrets") ?? "solutionfiles/secrets/secret.json",
        };
    }
}
