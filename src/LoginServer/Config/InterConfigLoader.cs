using System.Globalization;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Config;

public static class InterConfigLoader
{
    public static InterConfig Load(string path)
    {
        var host = "127.0.0.1";
        var port = 3306;
        var user = "";
        var pass = "";
        var db = "";
        var provider = "mysql";
        var loginAccountTable = "login";
        var ipbanTable = "ipbanlist";
        var loginLogTable = "loginlog";
        var globalAccRegNumTable = "global_acc_reg_num";
        var globalAccRegStrTable = "global_acc_reg_str";

        if (!File.Exists(path))
        {
            LoginLogger.Info($"Inter config not found: {path}. Using defaults.");
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

            if (key.Equals("login_server_ip", StringComparison.OrdinalIgnoreCase))
            {
                host = value;
            }
            else if (key.Equals("login_server_port", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    port = parsed;
                }
            }
            else if (key.Equals("login_server_id", StringComparison.OrdinalIgnoreCase))
            {
                user = value;
            }
            else if (key.Equals("login_server_pw", StringComparison.OrdinalIgnoreCase))
            {
                pass = value;
            }
            else if (key.Equals("login_server_db", StringComparison.OrdinalIgnoreCase))
            {
                db = value;
            }
            else if (key.Equals("login_db_provider", StringComparison.OrdinalIgnoreCase))
            {
                provider = value;
            }
            else if (key.Equals("login_server_account_db", StringComparison.OrdinalIgnoreCase))
            {
                loginAccountTable = value;
            }
            else if (key.Equals("ipban_table", StringComparison.OrdinalIgnoreCase))
            {
                ipbanTable = value;
            }
            else if (key.Equals("log_login_db", StringComparison.OrdinalIgnoreCase))
            {
                loginLogTable = value;
            }
            else if (key.Equals("global_acc_reg_num_table", StringComparison.OrdinalIgnoreCase))
            {
                globalAccRegNumTable = value;
            }
            else if (key.Equals("global_acc_reg_str_table", StringComparison.OrdinalIgnoreCase))
            {
                globalAccRegStrTable = value;
            }
        }

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(db))
        {
            return new InterConfig();
        }

        var normalizedProvider = provider.Trim().ToLowerInvariant();
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
            LoginDbProvider = normalizedProvider,
            LoginDbConnectionString = connectionString,
            LoginAccountTable = loginAccountTable,
            IpBanTable = ipbanTable,
            LoginLogTable = loginLogTable,
            GlobalAccRegNumTable = globalAccRegNumTable,
            GlobalAccRegStrTable = globalAccRegStrTable,
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
                    LoginLogger.Warning($"Inter config import not found: {resolved}. Skipping.");
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
}
