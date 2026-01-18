using System.Net;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Config;

public static class SubnetConfigLoader
{
    public static SubnetConfig Load(string path)
    {
        var config = new SubnetConfig();
        if (!File.Exists(path))
        {
            LoginLogger.Info($"Subnet config not found: {path}. Using defaults.");
            return config;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadConfigLines(path, visited))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith("subnet", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var rest = line[(separator + 1)..].Trim();
            var parts = rest.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            if (!TryParseIpv4(parts[0], out var mask))
            {
                continue;
            }

            if (!TryParseIpv4(parts[1], out var charIp))
            {
                continue;
            }

            if (!TryParseIpv4(parts[2], out var mapIp))
            {
                continue;
            }

            config.Add(new SubnetEntry(mask, charIp, mapIp));
        }

        return config;
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
                    LoginLogger.Warning($"Subnet config import not found: {resolved}. Skipping.");
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

    private static bool TryParseIpv4(string ip, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return true;
    }
}
