using System.Net;
using Athena.Net.MapServer.Gameplay.Rules;

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
}
