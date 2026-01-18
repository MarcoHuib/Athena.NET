using System.Net;

namespace Athena.Net.CharServer.Config;

public sealed class CharConfig
{
    public string UserId { get; init; } = "s1";
    public string Password { get; init; } = "p1";
    public string ServerName { get; init; } = "rAthena";
    public IPAddress LoginIp { get; init; } = IPAddress.Loopback;
    public int LoginPort { get; init; } = 6900;
    public IPAddress BindIp { get; init; } = IPAddress.Any;
    public IPAddress CharIp { get; init; } = IPAddress.Loopback;
    public int CharPort { get; init; } = 6121;
    public int CharMaintenance { get; init; }
    public bool CharNew { get; init; } = true;
    public int CharNewDisplay { get; init; }
    public int MinChars { get; init; } = 9;
    public int MaxChars { get; init; } = 15;
    public int CharDeleteDelaySeconds { get; init; } = 86400;
    public int CharDeleteLevel { get; init; }
    public int CharDeleteOption { get; init; } = 2;
    public int CharDeleteRestriction { get; init; } = 3;
    public int StartZeny { get; init; }
    public int StartStatusPoints { get; init; } = 48;
    public IReadOnlyList<StartPoint> StartPoints { get; init; } = Array.Empty<StartPoint>();
    public IReadOnlyList<StartPoint> StartPointsDoram { get; init; } = Array.Empty<StartPoint>();
    public IReadOnlyList<StartPoint> StartPointsPre { get; init; } = Array.Empty<StartPoint>();
    public IReadOnlyList<StartItem> StartItems { get; init; } = Array.Empty<StartItem>();
    public IReadOnlyList<StartItem> StartItemsDoram { get; init; } = Array.Empty<StartItem>();
    public IReadOnlyList<StartItem> StartItemsPre { get; init; } = Array.Empty<StartItem>();
    public bool UsePreRenewalStartPoints { get; init; }
    public bool ConsoleEnabled { get; init; }
    public int ConsoleMsgLog { get; init; }
    public int ConsoleSilent { get; init; }
    public string ConsoleLogFilePath { get; init; } = "./log/char-msg_log.log";
    public string TimestampFormat { get; init; } = string.Empty;
}
