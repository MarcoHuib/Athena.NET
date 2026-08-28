using System.Net;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rates;

namespace Athena.Net.MapServer.Config;

public sealed class MapConfig
{
    public string UserId { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public IPAddress CharIp { get; init; } = IPAddress.Loopback;
    public int CharPort { get; init; } = 6121;
    public IPAddress BindIp { get; init; } = IPAddress.Any;
    public IPAddress MapIp { get; init; } = IPAddress.Loopback;
    public int MapPort { get; init; } = 5121;
    public string ConsoleLogFilePath { get; init; } = "./log/map-msg_log.log";
    public bool ConsoleEnabled { get; init; }
    public int ConsoleMsgLog { get; init; }
    public int ConsoleSilent { get; init; }
    public string TimestampFormat { get; init; } = string.Empty;
    // Athena.NET currently targets the current official iRO client, which is
    // RENEWAL-only - see ai/map-server.md's "Gameplay ruleset selection" section.
    // "gameplay_ruleset" in map_athena.conf; defaults to Renewal (GameplayOptions'
    // own default) when unset or unrecognized, matching MapConfigLoader's existing
    // "use the field default on a bad/missing value" convention for every other key.
    public RagnarokRuleSet GameplayRuleSet { get; init; } = RagnarokRuleSet.Renewal;
    public GameplayRateOptions GameplayRates { get; init; } = new();

    // Zero or more "map_collision_artifact: <path>|<map1>,<map2>,..." lines. Each entry names one
    // locally supplied Athena collision artifact file (see ai/world-data.md "Map collision data
    // import + runtime collision foundation") and the logical Athena map name(s) it should be
    // registered under - multiple names because several logical map declarations can share one
    // physical collision resource. This is SECONDARY/debug tooling now that "map_cache_path" below
    // is the normal source (see ai/world-data.md) - it remains available for a locally supplied
    // .gat-derived artifact when that is genuinely useful (debugging the compiler, a map absent
    // from the pinned map_cache.dat, etc.). Empty by default: no map in this repository ships a
    // committed artifact, so MapServerWorld.Build's default EmptyMapCollisionProvider remains
    // exactly what an unconfigured server gets.
    public IReadOnlyList<MapCollisionArtifactConfig> CollisionArtifacts { get; init; } = [];

    // "map_cache_path: <path>" - the NORMAL Athena world-geometry source (see ai/world-data.md):
    // pinned rAthena's own db/map_cache.dat, read directly at startup by RathenaMapCacheReader,
    // covering every map declared in that file in one load - no per-map conversion step, no
    // installed client, no GRF. Null (unset) by default; configuring both this AND one or more
    // map_collision_artifact lines is a startup configuration error (MapCollisionStartupLoader
    // throws) rather than an implicit precedence rule, since silently picking one source over the
    // other could hide a real operator mistake.
    public string? MapCachePath { get; init; }
}

public sealed record MapCollisionArtifactConfig(string Path, IReadOnlyList<string> Maps);
