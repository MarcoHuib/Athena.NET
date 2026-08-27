using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Logging;

namespace Athena.Net.MapServer.Config;

public static class MapConfigLoader
{
    public static MapConfig Load(string path)
    {
        var userId = string.Empty;
        var password = string.Empty;
        var charIp = IPAddress.Loopback;
        var charPort = 6121;
        var bindIp = IPAddress.Any;
        var mapIp = IPAddress.Loopback;
        var mapPort = 5121;
        var consoleEnabled = false;
        var consoleMsgLog = 0;
        var consoleSilent = 0;
        var consoleLogFilePath = "./log/map-msg_log.log";
        var timestampFormat = string.Empty;
        var gameplayRuleSet = RagnarokRuleSet.Renewal;
        var collisionArtifacts = new List<MapCollisionArtifactConfig>();
        string? mapCachePath = null;

        if (!File.Exists(path))
        {
            MapLogger.Info($"Config not found: {path}. Using defaults.");
            return new MapConfig
            {
                UserId = userId,
                Password = password,
                CharIp = charIp,
                CharPort = charPort,
                BindIp = bindIp,
                MapIp = mapIp,
                MapPort = mapPort,
                ConsoleEnabled = consoleEnabled,
                ConsoleMsgLog = consoleMsgLog,
                ConsoleSilent = consoleSilent,
                ConsoleLogFilePath = consoleLogFilePath,
                TimestampFormat = timestampFormat,
                GameplayRuleSet = gameplayRuleSet,
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
            else if (key.Equals("char_ip", StringComparison.OrdinalIgnoreCase))
            {
                if (TryResolveIp(value, out var ip))
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
            else if (key.Equals("bind_ip", StringComparison.OrdinalIgnoreCase))
            {
                if (IPAddress.TryParse(value, out var ip))
                {
                    bindIp = ip;
                }
            }
            else if (key.Equals("map_ip", StringComparison.OrdinalIgnoreCase))
            {
                if (TryResolveIp(value, out var ip))
                {
                    mapIp = ip;
                }
            }
            else if (key.Equals("map_port", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    mapPort = parsed;
                }
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
            else if (key.Equals("gameplay_ruleset", StringComparison.OrdinalIgnoreCase))
            {
                // Unlike every other key in this loader, an explicitly present but
                // unrecognized value here must NOT silently fall back to the default -
                // a typo'd "gameplay_ruleset: Renewel" must never quietly run as Renewal
                // (masking the operator's mistake) and must never quietly resolve to
                // PreRenewal (which is a real enum value but architecturally
                // unimplemented - see GameplayRulesFactory). PreRenewal itself DOES parse
                // successfully here; it fails later at composition (GameplayRulesFactory),
                // not at config-parse time.
                if (!Enum.TryParse<RagnarokRuleSet>(value, ignoreCase: true, out gameplayRuleSet))
                {
                    throw new InvalidOperationException(
                        $"Invalid gameplay_ruleset value '{value}' in '{path}'. Valid values: {string.Join(", ", Enum.GetNames<RagnarokRuleSet>())}.");
                }
            }
            else if (key.Equals("map_collision_artifact", StringComparison.OrdinalIgnoreCase))
            {
                // Repeatable key (unlike every other key here, which overwrites a single scalar):
                // "map_collision_artifact: <path>|<map1>,<map2>,...". Malformed lines fail loudly
                // rather than being silently skipped - an operator who mistypes this line must not
                // end up with a server that quietly runs without the collision data they configured.
                var pipe = value.IndexOf('|');
                if (pipe <= 0 || pipe == value.Length - 1)
                {
                    throw new InvalidOperationException(
                        $"Invalid map_collision_artifact value '{value}' in '{path}'. Expected '<path>|<map1>,<map2>,...'.");
                }

                var artifactPath = value[..pipe].Trim();
                var maps = value[(pipe + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (artifactPath.Length == 0 || maps.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Invalid map_collision_artifact value '{value}' in '{path}'. Expected '<path>|<map1>,<map2>,...'.");
                }

                collisionArtifacts.Add(new MapCollisionArtifactConfig(artifactPath, maps));
            }
            else if (key.Equals("map_cache_path", StringComparison.OrdinalIgnoreCase))
            {
                mapCachePath = value;
            }
        }

        if (mapCachePath is { Length: > 0 } && collisionArtifacts.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{path}' configures both map_cache_path and map_collision_artifact. Configure only one map geometry source.");
        }

        return new MapConfig
        {
            UserId = userId,
            Password = password,
            CharIp = charIp,
            CharPort = charPort,
            BindIp = bindIp,
            MapIp = mapIp,
            MapPort = mapPort,
            ConsoleEnabled = consoleEnabled,
            ConsoleMsgLog = consoleMsgLog,
            ConsoleSilent = consoleSilent,
            ConsoleLogFilePath = consoleLogFilePath,
            TimestampFormat = timestampFormat,
            GameplayRuleSet = gameplayRuleSet,
            CollisionArtifacts = collisionArtifacts,
            MapCachePath = mapCachePath,
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
                    MapLogger.Warning($"Map config import not found: {resolved}. Skipping.");
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

    private static bool TryResolveIp(string value, out IPAddress ip)
    {
        if (IPAddress.TryParse(value, out var parsedIp) && parsedIp is not null)
        {
            ip = parsedIp;
            return true;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(value);
            foreach (var address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    ip = address;
                    return true;
                }
            }

            if (addresses.Length > 0)
            {
                ip = addresses[0];
                return true;
            }
        }
        catch
        {
        }

        ip = IPAddress.Any;
        return false;
    }
}
