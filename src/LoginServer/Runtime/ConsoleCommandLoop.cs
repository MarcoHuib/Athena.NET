using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Logging;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Runtime;

public static class ConsoleCommandLoop
{
    public static Task StartAsync(LoginConfigStore configStore, LoginMessageStore loginMessages, string interConfigPath, CharServerRegistry charServers, LoginState state, Func<LoginDbContext?> dbFactory, IServiceProvider serviceProvider, CancellationTokenSource cts)
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
                    await CreateAccountFromConsoleAsync(raw, serviceProvider, cts.Token);
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

    /// <summary>
    /// Creates a player account through IPlayerAccountProvisioningService (Identity
    /// user + AthenaGameAccount, one transaction) rather than writing directly into
    /// the legacy login table, which player login no longer reads. A fresh DI scope
    /// is created per invocation rather than resolving from a scope held for this
    /// long-lived console loop's entire lifetime.
    /// </summary>
    private static async Task CreateAccountFromConsoleAsync(string command, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var payload = command.StartsWith("create:", StringComparison.OrdinalIgnoreCase)
            ? command[7..].Trim()
            : command[6..].Trim();

        var parts = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            LoginLogger.Status("Usage: create:<username> <password> <sex:M|F> [email]");
            return;
        }

        var user = parts[0];
        var pass = parts[1];
        var sex = char.ToUpperInvariant(parts[2][0]);
        var email = parts.Length >= 4 ? parts[3] : $"{user}@players.athena.local";

        if (user.Length < 4 || pass.Length < 1 || (sex != 'M' && sex != 'F'))
        {
            LoginLogger.Warning("Invalid parameters. Usage: create:<username> <password> <sex:M|F> [email]");
            return;
        }

        using var scope = serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetService<IPlayerAccountProvisioningService>();
        if (provisioning == null)
        {
            LoginLogger.Error("Identity DB: unavailable, cannot create a player account.");
            return;
        }

        var result = await provisioning.ProvisionAsync(user, email, pass, sex, cancellationToken);
        if (!result.Success)
        {
            LoginLogger.Warning($"Account '{user}' was not created: {result.ErrorMessage}");
            return;
        }

        LoginLogger.Status($"Account '{user}' created (RagnarokAccountId={result.RagnarokAccountId}).");
    }
}
