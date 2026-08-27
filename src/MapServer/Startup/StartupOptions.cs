namespace Athena.Net.MapServer.Startup;

public sealed class StartupOptions
{
    public string ConfigPath { get; init; } = "conf/map_athena.conf";
    public string SecretsPath { get; init; } = "solutionfiles/secrets/secret.json";

    // Explicit runtime override for the configured `map_cache_path` config value - filesystem
    // resource resolution is a deployment/runtime concern that must not accidentally depend on
    // process CWD (see MapServerApp.RunAsync's own doc comment on the precedence rule this
    // implements). Null (unset) is the normal case for direct local execution from the repo root
    // and for Docker (both already have a CWD-relative `map_cache_path` config value that resolves
    // correctly there) - only a launcher/orchestrator that knows a definite absolute path but
    // cannot guarantee this process's CWD (Aspire's AppHost, which discovers the repo root itself
    // and already passes other config paths as absolutes the same way) needs to supply this.
    public string? MapCachePathOverride { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        return new StartupOptions
        {
            ConfigPath = ArgsHelper.GetValue(args, "--map-config") ?? "conf/map_athena.conf",
            SecretsPath = ArgsHelper.GetValue(args, "--secrets") ?? "solutionfiles/secrets/secret.json",
            MapCachePathOverride = ArgsHelper.GetValue(args, "--map-cache-path"),
        };
    }
}
