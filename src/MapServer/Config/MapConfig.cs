using System.Net;

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
}
