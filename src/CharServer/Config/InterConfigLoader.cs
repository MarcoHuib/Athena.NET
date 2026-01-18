using System.Globalization;
using Athena.Net.CharServer.Logging;

namespace Athena.Net.CharServer.Config;

public static class InterConfigLoader
{
    public static InterConfig Load(string path)
    {
        var host = "127.0.0.1";
        var port = 3306;
        var user = "";
        var pass = "";
        var db = "";
        var provider = string.Empty;
        var charTable = "char";
        var inventoryTable = "inventory";
        var skillTable = "skill";
        var hotkeyTable = "hotkey";
        var startStatusPoints = 48;

        if (!File.Exists(path))
        {
            CharLogger.Info($"Inter config not found: {path}. Using defaults.");
            return new InterConfig();
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

            if (key.Equals("char_server_ip", StringComparison.OrdinalIgnoreCase))
            {
                host = value;
            }
            else if (key.Equals("char_server_port", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    port = parsed;
                }
            }
            else if (key.Equals("char_server_id", StringComparison.OrdinalIgnoreCase))
            {
                user = value;
            }
            else if (key.Equals("char_server_pw", StringComparison.OrdinalIgnoreCase))
            {
                pass = value;
            }
            else if (key.Equals("char_server_db", StringComparison.OrdinalIgnoreCase))
            {
                db = value;
            }
            else if (key.Equals("char_db_provider", StringComparison.OrdinalIgnoreCase))
            {
                provider = value;
            }
            else if (key.Equals("login_db_provider", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(provider))
            {
                provider = value;
            }
            else if (key.Equals("char_db", StringComparison.OrdinalIgnoreCase))
            {
                charTable = value;
            }
            else if (key.Equals("inventory_db", StringComparison.OrdinalIgnoreCase))
            {
                inventoryTable = value;
            }
            else if (key.Equals("skill_db", StringComparison.OrdinalIgnoreCase))
            {
                skillTable = value;
            }
            else if (key.Equals("hotkey_db", StringComparison.OrdinalIgnoreCase))
            {
                hotkeyTable = value;
            }
            else if (key.Equals("start_status_points", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    startStatusPoints = parsed;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(db))
        {
            return new InterConfig
            {
                CharDbProvider = provider,
                CharTable = charTable,
                InventoryTable = inventoryTable,
                SkillTable = skillTable,
                HotkeyTable = hotkeyTable,
                StartStatusPoints = startStatusPoints,
            };
        }

        var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? "mysql" : provider.Trim().ToLowerInvariant();
        string connectionString;
        if (normalizedProvider == "sqlserver" || normalizedProvider == "mssql")
        {
            var server = port > 0 ? $"{host},{port}" : host;
            connectionString = $"Server={server};Database={db};User ID={user};Password={pass};Encrypt=True;TrustServerCertificate=True;";
        }
        else
        {
            connectionString = $"Server={host};Port={port};Database={db};User={user};Password={pass};SslMode=None;";
        }

        return new InterConfig
        {
            CharDbProvider = normalizedProvider,
            CharDbConnectionString = connectionString,
            CharTable = charTable,
            InventoryTable = inventoryTable,
            SkillTable = skillTable,
            HotkeyTable = hotkeyTable,
            StartStatusPoints = startStatusPoints,
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
                    CharLogger.Warning($"Inter config import not found: {resolved}. Skipping.");
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
}
