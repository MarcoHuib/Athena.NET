using System.Globalization;

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
            Console.WriteLine($"Inter config not found: {path}. Using defaults.");
            return new InterConfig();
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = StripComment(rawLine).Trim();
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

        var connectionString = $"Server={host};Port={port};Database={db};User={user};Password={pass};SslMode=None;";

        return new InterConfig
        {
            LoginDbProvider = provider,
            LoginDbConnectionString = connectionString,
            LoginAccountTable = loginAccountTable,
            IpBanTable = ipbanTable,
            LoginLogTable = loginLogTable,
            GlobalAccRegNumTable = globalAccRegNumTable,
            GlobalAccRegStrTable = globalAccRegStrTable,
        };
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }
}
