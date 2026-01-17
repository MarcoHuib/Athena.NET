using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Logging;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Runtime;

public static class ConsoleCommandLoop
{
    public static Task StartAsync(LoginConfigStore configStore, LoginMessageStore loginMessages, string interConfigPath, CharServerRegistry charServers, LoginState state, Func<LoginDbContext?> dbFactory, CancellationTokenSource cts)
    {
        if (!configStore.Current.ConsoleEnabled)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await Console.In.ReadLineAsync();
                }
                catch (IOException)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var trimmed = line.Trim();
                var type = trimmed;
                var command = string.Empty;
                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex >= 0)
                {
                    type = trimmed[..colonIndex].Trim();
                    command = trimmed[(colonIndex + 1)..].Trim();
                }

                var cmd = type.ToLowerInvariant();
                if (cmd == "server")
                {
                    var sub = command.ToLowerInvariant();
                    if (sub == "shutdown" || sub == "exit" || sub == "quit")
                    {
                        LoginLogger.Status("Shutdown requested.");
                        cts.Cancel();
                    }
                    else if (sub == "alive" || sub == "status")
                    {
                        var servers = charServers.All().Count();
                        LoginLogger.Status($"Status: online={state.OnlineCount}, auth={state.AuthCount}, char_servers={servers}");
                    }
                    else if (sub == "reloadconf")
                    {
                        ReloadConfig(configStore, loginMessages, interConfigPath);
                    }
                    else
                    {
                        LoginLogger.Status("Server commands: shutdown, status, reloadconf");
                    }
                }
                else if (cmd == "create" || cmd.StartsWith("create", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = colonIndex >= 0 ? $"create:{command}" : trimmed;
                    await CreateAccountFromConsoleAsync(raw, configStore, dbFactory, cts.Token);
                }
                else if (cmd == "quit" || cmd == "exit" || cmd == "shutdown")
                {
                    LoginLogger.Status("Shutdown requested.");
                    cts.Cancel();
                }
                else if (cmd == "reload")
                {
                    ReloadConfig(configStore, loginMessages, interConfigPath);
                }
                else if (cmd == "status")
                {
                    var servers = charServers.All().Count();
                    LoginLogger.Status($"Status: online={state.OnlineCount}, auth={state.AuthCount}, char_servers={servers}");
                }
                else
                {
                    LoginLogger.Status("Commands: status, reload, quit, create:<username> <password> <sex> | server:shutdown|status|reloadconf");
                }
            }
        }, cts.Token);
    }

    private static void ReloadConfig(LoginConfigStore configStore, LoginMessageStore loginMessages, string interConfigPath)
    {
        var ok = configStore.Reload();
        if (ok)
        {
            LoginLogger.Configure(configStore.Current);
        }

        var msgOk = loginMessages.Reload();
        var reloadedInter = InterConfigLoader.Load(interConfigPath);
        configStore.UpdateLoginCaseSensitive(reloadedInter.LoginCaseSensitive);
        LoginLogger.Status(ok && msgOk ? "Config reloaded." : "Config reload failed.");
    }

    private static async Task CreateAccountFromConsoleAsync(string command, LoginConfigStore configStore, Func<LoginDbContext?> dbFactory, CancellationToken cancellationToken)
    {
        var payload = command.StartsWith("create:", StringComparison.OrdinalIgnoreCase)
            ? command[7..].Trim()
            : command[6..].Trim();

        var parts = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            LoginLogger.Status("Usage: create:<username> <password> <sex:M|F>");
            return;
        }

        var user = parts[0];
        var pass = parts[1];
        var sex = parts[2][0];
        sex = char.ToUpperInvariant(sex);

        if (user.Length < 4 || pass.Length < 1 || (sex != 'M' && sex != 'F'))
        {
            LoginLogger.Warning("Invalid parameters. Usage: create:<username> <password> <sex:M|F>");
            return;
        }

        var db = dbFactory();
        if (db == null)
        {
            LoginLogger.Error("DB: unable to create connection.");
            return;
        }

        await using (db)
        {
            var exists = await ExistsUserIdAsync(db, configStore, user, cancellationToken);
            if (exists)
            {
                LoginLogger.Warning($"Account '{user}' already exists.");
                return;
            }

            if (configStore.Current.UseMd5Passwords)
            {
                pass = Md5Hex(Encoding.ASCII.GetBytes(pass));
            }

            var expiration = 0u;
            if (configStore.Current.StartLimitedTimeSeconds != -1)
            {
                expiration = (uint)new DateTimeOffset(DateTime.UtcNow.AddSeconds(configStore.Current.StartLimitedTimeSeconds))
                    .ToUnixTimeSeconds();
            }

            var account = new Db.Entities.LoginAccount
            {
                UserId = user,
                UserPass = pass,
                Sex = sex.ToString(),
                Email = "a@a.com",
                ExpirationTime = expiration,
                LastLogin = null,
                LastIp = "0.0.0.0",
                Birthdate = null,
                Pincode = string.Empty,
                PincodeChange = 0,
                CharacterSlots = (byte)configStore.Current.CharPerAccount,
                VipTime = 0,
                OldGroup = 0,
                GroupId = 0,
                State = 0,
                LoginCount = 0,
                WebAuthToken = null,
                WebAuthTokenEnabled = false,
            };

            db.Accounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);
            LoginLogger.Status($"Account '{user}' created.");
        }
    }

    private static async Task<bool> ExistsUserIdAsync(LoginDbContext db, LoginConfigStore configStore, string userId, CancellationToken cancellationToken)
    {
        if (configStore.LoginCaseSensitive)
        {
            return await db.Accounts.AsNoTracking().AnyAsync(a => a.UserId == userId, cancellationToken);
        }

        var normalizedUserId = userId.ToLowerInvariant();
        return await db.Accounts.AsNoTracking().AnyAsync(a => a.UserId.ToLower() == normalizedUserId, cancellationToken);
    }

    private static string Md5Hex(byte[] data)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
