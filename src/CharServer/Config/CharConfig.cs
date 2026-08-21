using System.Net;

namespace Athena.Net.CharServer.Config;

public sealed class CharConfig
{
    public bool IroRenewalCompatibility { get; init; } = !string.Equals(
        Environment.GetEnvironmentVariable("ATHENA_NET_CHAR_IRO_RENEWAL"),
        "false",
        StringComparison.OrdinalIgnoreCase);
    public IPAddress IroAdvertisedMapIp { get; init; } = ParseIpEnvironment(
        "ATHENA_NET_CHAR_IRO_MAP_IP", "128.241.92.42");
    public int IroAdvertisedMapPort { get; init; } = ParseIntEnvironment(
        "ATHENA_NET_CHAR_IRO_MAP_PORT", 4501);
    public string UserId { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
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
    public string WispServerName { get; init; } = "Server";
    public string UnknownCharName { get; init; } = "Unknown";
    public bool NameIgnoringCase { get; init; }
    public int CharNameOption { get; init; }
    public string CharNameLetters { get; init; } = string.Empty;
    public int CharNameMinLength { get; init; } = 4;
    public bool CharRenameParty { get; init; }
    public bool CharRenameGuild { get; init; }
    public bool PincodeEnabled { get; init; } = true;
    public int PincodeChangeTimeSeconds { get; init; }
    public int PincodeMaxTry { get; init; } = 3;
    public bool PincodeForce { get; init; } = true;
    public bool PincodeAllowRepeated { get; init; }
    public bool PincodeAllowSequential { get; init; }
    public bool CharMoveEnabled { get; init; } = true;
    public bool CharMoveToUsed { get; init; } = true;
    public bool CharMovesUnlimited { get; init; }
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

    private static IPAddress ParseIpEnvironment(string name, string fallback)
    {
        return IPAddress.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? value
            : IPAddress.Parse(fallback);
    }

    private static int ParseIntEnvironment(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value is > 0 and <= ushort.MaxValue
            ? value
            : fallback;
    }
}
