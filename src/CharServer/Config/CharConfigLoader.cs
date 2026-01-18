using System.Globalization;
using System.Net;
using Athena.Net.CharServer.Logging;

namespace Athena.Net.CharServer.Config;

public static class CharConfigLoader
{
    public static CharConfig Load(string path)
    {
        var userId = "s1";
        var password = "p1";
        var serverName = "rAthena";
        var loginIp = IPAddress.Loopback;
        var loginPort = 6900;
        var bindIp = IPAddress.Any;
        var charIp = IPAddress.Loopback;
        var charPort = 6121;
        var charMaintenance = 0;
        var charNew = true;
        var charNewDisplay = 0;
        var minChars = 9;
        var maxChars = 15;
        var charDeleteDelaySeconds = 86400;
        var charDeleteLevel = 0;
        var charDeleteOption = 2;
        var charDeleteRestriction = 3;
        var startZeny = 0;
        var startPoints = new List<StartPoint>();
        var startPointsDoram = new List<StartPoint>();
        var startPointsPre = new List<StartPoint>();
        var startItems = new List<StartItem>();
        var startItemsDoram = new List<StartItem>();
        var startItemsPre = new List<StartItem>();
        var usePreRenewalStartPoints = string.Equals(
            Environment.GetEnvironmentVariable("ATHENA_NET_CHAR_PRE_RENEWAL"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var consoleEnabled = false;
        var consoleMsgLog = 0;
        var consoleSilent = 0;
        var consoleLogFilePath = "./log/char-msg_log.log";
        var timestampFormat = string.Empty;

        if (!File.Exists(path))
        {
            CharLogger.Info($"Config not found: {path}. Using defaults.");
            return new CharConfig
            {
                UserId = userId,
                Password = password,
                ServerName = serverName,
                LoginIp = loginIp,
                LoginPort = loginPort,
                BindIp = bindIp,
                CharIp = charIp,
                CharPort = charPort,
                CharMaintenance = charMaintenance,
                CharNew = charNew,
                CharNewDisplay = charNewDisplay,
                MinChars = minChars,
                MaxChars = maxChars,
                CharDeleteDelaySeconds = charDeleteDelaySeconds,
                CharDeleteLevel = charDeleteLevel,
                CharDeleteOption = charDeleteOption,
                CharDeleteRestriction = charDeleteRestriction,
                StartZeny = startZeny,
                StartStatusPoints = 48,
                StartPoints = startPoints,
                StartPointsDoram = startPointsDoram,
                StartPointsPre = startPointsPre,
                StartItems = startItems,
                StartItemsDoram = startItemsDoram,
                StartItemsPre = startItemsPre,
                UsePreRenewalStartPoints = usePreRenewalStartPoints,
                ConsoleEnabled = consoleEnabled,
                ConsoleMsgLog = consoleMsgLog,
                ConsoleSilent = consoleSilent,
                ConsoleLogFilePath = consoleLogFilePath,
                TimestampFormat = timestampFormat,
            };
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadConfigLines(path, visited))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (key.Equals("userid", StringComparison.OrdinalIgnoreCase))
            {
                userId = value;
            }
            else if (key.Equals("passwd", StringComparison.OrdinalIgnoreCase))
            {
                password = value;
            }
            else if (key.Equals("server_name", StringComparison.OrdinalIgnoreCase))
            {
                serverName = value;
            }
            else if (key.Equals("login_ip", StringComparison.OrdinalIgnoreCase))
            {
                if (IPAddress.TryParse(value, out var ip))
                {
                    loginIp = ip;
                }
            }
            else if (key.Equals("login_port", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    loginPort = parsed;
                }
            }
            else if (key.Equals("bind_ip", StringComparison.OrdinalIgnoreCase))
            {
                if (IPAddress.TryParse(value, out var ip))
                {
                    bindIp = ip;
                }
            }
            else if (key.Equals("char_ip", StringComparison.OrdinalIgnoreCase))
            {
                if (IPAddress.TryParse(value, out var ip))
                {
                    charIp = ip;
                }
            }
            else if (key.Equals("char_port", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    charPort = parsed;
                }
            }
            else if (key.Equals("char_maintenance", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    charMaintenance = parsed;
                }
            }
            else if (key.Equals("char_new", StringComparison.OrdinalIgnoreCase))
            {
                charNew = ParseBool(value, charNew);
            }
            else if (key.Equals("char_new_display", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    charNewDisplay = parsed;
                }
            }
            else if (key.Equals("char_del_delay", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    charDeleteDelaySeconds = parsed;
                }
            }
            else if (key.Equals("char_del_level", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    charDeleteLevel = parsed;
                }
            }
            else if (key.Equals("char_del_option", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    charDeleteOption = parsed;
                }
            }
            else if (key.Equals("char_del_restriction", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    charDeleteRestriction = parsed;
                }
            }
            else if (key.Equals("start_zeny", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    startZeny = Math.Max(0, parsed);
                }
            }
            else if (key.Equals("start_point", StringComparison.OrdinalIgnoreCase))
            {
                startPoints = ParseStartPoints(value);
            }
            else if (key.Equals("start_point_pre", StringComparison.OrdinalIgnoreCase))
            {
                startPointsPre = ParseStartPoints(value);
            }
            else if (key.Equals("start_point_doram", StringComparison.OrdinalIgnoreCase))
            {
                startPointsDoram = ParseStartPoints(value);
            }
            else if (key.Equals("start_items", StringComparison.OrdinalIgnoreCase))
            {
                startItems = ParseStartItems(value);
            }
            else if (key.Equals("start_items_pre", StringComparison.OrdinalIgnoreCase))
            {
                startItemsPre = ParseStartItems(value);
            }
            else if (key.Equals("start_items_doram", StringComparison.OrdinalIgnoreCase))
            {
                startItemsDoram = ParseStartItems(value);
            }
            else if (key.Equals("console", StringComparison.OrdinalIgnoreCase))
            {
                consoleEnabled = ParseBool(value, consoleEnabled);
            }
            else if (key.Equals("console_msg_log", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    consoleMsgLog = parsed;
                }
            }
            else if (key.Equals("console_silent", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    consoleSilent = parsed;
                }
            }
            else if (key.Equals("console_log_filepath", StringComparison.OrdinalIgnoreCase))
            {
                consoleLogFilePath = value;
            }
            else if (key.Equals("timestamp_format", StringComparison.OrdinalIgnoreCase))
            {
                timestampFormat = value;
            }
        }

        if (startPoints.Count == 0)
        {
            startPoints.Add(new StartPoint("iz_int", 18, 26));
        }

        if (startPointsDoram.Count == 0)
        {
            startPointsDoram.AddRange(startPoints);
        }

        if (startItemsDoram.Count == 0)
        {
            startItemsDoram.AddRange(startItems);
        }

        if (startPointsPre.Count == 0)
        {
            startPointsPre.AddRange(startPoints);
        }

        if (startItemsPre.Count == 0)
        {
            startItemsPre.AddRange(startItems);
        }

        return new CharConfig
        {
            UserId = userId,
            Password = password,
            ServerName = serverName,
            LoginIp = loginIp,
            LoginPort = loginPort,
            BindIp = bindIp,
            CharIp = charIp,
            CharPort = charPort,
            CharMaintenance = charMaintenance,
            CharNew = charNew,
            CharNewDisplay = charNewDisplay,
            MinChars = minChars,
            MaxChars = maxChars,
            CharDeleteDelaySeconds = charDeleteDelaySeconds,
            CharDeleteLevel = charDeleteLevel,
            CharDeleteOption = charDeleteOption,
            CharDeleteRestriction = charDeleteRestriction,
            StartZeny = startZeny,
            StartStatusPoints = 48,
            StartPoints = startPoints,
            StartPointsDoram = startPointsDoram,
            StartPointsPre = startPointsPre,
            StartItems = startItems,
            StartItemsDoram = startItemsDoram,
            StartItemsPre = startItemsPre,
            UsePreRenewalStartPoints = usePreRenewalStartPoints,
            ConsoleEnabled = consoleEnabled,
            ConsoleMsgLog = consoleMsgLog,
            ConsoleSilent = consoleSilent,
            ConsoleLogFilePath = consoleLogFilePath,
            TimestampFormat = timestampFormat,
        };
    }

    private static IEnumerable<string> ReadConfigLines(string path, HashSet<string> visited)
    {
        var fullPath = Path.GetFullPath(path);
        if (!visited.Add(fullPath))
        {
            yield break;
        }

        foreach (var rawLine in File.ReadLines(fullPath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryGetImportPath(line, out var importPath))
            {
                var resolved = ResolveImportPath(fullPath, importPath);
                if (!File.Exists(resolved))
                {
                    CharLogger.Warning($"Char config import not found: {resolved}. Skipping.");
                    continue;
                }

                foreach (var imported in ReadConfigLines(resolved, visited))
                {
                    yield return imported;
                }

                continue;
            }

            yield return line;
        }
    }

    private static bool TryGetImportPath(string line, out string importPath)
    {
        importPath = string.Empty;
        var separator = line.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        var key = line[..separator].Trim();
        if (!key.Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        importPath = line[(separator + 1)..].Trim().Trim('"');
        while (importPath.StartsWith("conf/", StringComparison.OrdinalIgnoreCase) ||
               importPath.StartsWith("conf\\", StringComparison.OrdinalIgnoreCase))
        {
            importPath = importPath[5..];
        }
        return importPath.Length > 0;
    }

    private static string ResolveImportPath(string basePath, string importPath)
    {
        if (!Path.IsPathRooted(importPath))
        {
            while (importPath.StartsWith("conf/", StringComparison.OrdinalIgnoreCase) ||
                   importPath.StartsWith("conf\\", StringComparison.OrdinalIgnoreCase))
            {
                importPath = importPath[5..];
            }
        }

        if (Path.IsPathRooted(importPath))
        {
            return importPath;
        }

        var cwdCandidate = Path.GetFullPath(importPath);
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        var dir = Path.GetDirectoryName(basePath);
        return Path.GetFullPath(Path.Combine(dir ?? ".", importPath));
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }

    private static bool ParseBool(string value, bool fallback)
    {
        if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fallback;
    }

    private static List<StartPoint> ParseStartPoints(string value)
    {
        var points = new List<StartPoint>();
        foreach (var entry in value.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var map = parts[0];
            if (!ushort.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x))
            {
                continue;
            }

            if (!ushort.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                continue;
            }

            points.Add(new StartPoint(map, x, y));
        }

        return points;
    }

    private static List<StartItem> ParseStartItems(string value)
    {
        var items = new List<StartItem>();
        foreach (var entry in value.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            if (!uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
            {
                continue;
            }

            if (!ushort.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            {
                continue;
            }

            if (!uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var equipPosition))
            {
                equipPosition = 0;
            }

            if (itemId == 0 || amount == 0)
            {
                continue;
            }

            items.Add(new StartItem(itemId, amount, equipPosition));
        }

        return items;
    }
}
