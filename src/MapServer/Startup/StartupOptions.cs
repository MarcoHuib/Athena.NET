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

    // Item 14: explicit runtime override for the configured `map_port` config value - same
    // "deployment/runtime concern, never a config-file edit" pattern as MapCachePathOverride above.
    // Null (unset) is the normal single-MapServer case (config-file `map_port` remains the sole
    // source, exactly as before this override existed - single-MapServer defaults are unchanged).
    // Only a caller running MULTIPLE MapServer processes against one shared `map_athena.conf` (the
    // local two-replica Aspire topology - see AppHost's own map-server-b resource) needs to supply
    // this, so each process binds a distinct port without needing a second config file.
    public int? MapPortOverride { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        var mapPortText = ArgsHelper.GetValue(args, "--map-port");
        return new StartupOptions
        {
            ConfigPath = ArgsHelper.GetValue(args, "--map-config") ?? "conf/map_athena.conf",
            SecretsPath = ArgsHelper.GetValue(args, "--secrets") ?? "solutionfiles/secrets/secret.json",
            MapCachePathOverride = ArgsHelper.GetValue(args, "--map-cache-path"),
            MapPortOverride = mapPortText is null ? null : int.Parse(mapPortText),
        };
    }
}
