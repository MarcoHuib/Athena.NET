using Athena.Net.MapServer.Startup;

namespace Athena.Net.MapServer.Tests.Startup;

// Regression coverage for the Aspire map_cache_path incident: filesystem resource resolution must
// not accidentally depend on process CWD. --map-cache-path is the explicit runtime override
// MapServerApp.RunAsync prefers over the configured map_cache_path value (see that method's own
// doc comment on the precedence rule: `options.MapCachePathOverride ?? mergedConfig.MapCachePath`)
// - these tests cover the CLI-parsing half of that rule; MapCollisionStartupLoaderTests covers the
// loader accepting whatever effective path results (absolute or relative) uniformly.
public sealed class StartupOptionsTests
{
    [Fact]
    public void Parse_NoMapCachePathArg_OverrideIsNull()
    {
        var options = StartupOptions.Parse(["--map-config", "conf/map_athena.conf"]);

        Assert.Null(options.MapCachePathOverride);
    }

    [Fact]
    public void Parse_MapCachePathArg_SetsOverride()
    {
        var options = StartupOptions.Parse(["--map-cache-path", "/repo/legacy/rathena/db/map_cache.dat"]);

        Assert.Equal("/repo/legacy/rathena/db/map_cache.dat", options.MapCachePathOverride);
    }

    [Fact]
    public void Parse_MapCachePathArg_DoesNotAffectOtherOptions()
    {
        var options = StartupOptions.Parse([
            "--map-config", "conf/map_athena.conf",
            "--map-cache-path", "/repo/legacy/rathena/db/map_cache.dat",
            "--secrets", "solutionfiles/secrets/secret.json",
        ]);

        Assert.Equal("conf/map_athena.conf", options.ConfigPath);
        Assert.Equal("solutionfiles/secrets/secret.json", options.SecretsPath);
        Assert.Equal("/repo/legacy/rathena/db/map_cache.dat", options.MapCachePathOverride);
    }

    [Fact]
    public void Parse_MapCachePathArg_AcceptsAnAbsolutePath_MatchingHowAspireSuppliesIt()
    {
        // Aspire's AppHost passes an absolute path it discovered itself (repoRoot-derived), not a
        // repository-relative one - this is the exact shape that must round-trip unchanged.
        const string absolutePath = "/Users/example/Athena.NET/legacy/rathena/db/map_cache.dat";

        var options = StartupOptions.Parse(["--map-cache-path", absolutePath]);

        Assert.Equal(absolutePath, options.MapCachePathOverride);
    }
}
