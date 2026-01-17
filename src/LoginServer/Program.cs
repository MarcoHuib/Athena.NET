using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Net;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configPath = ArgsHelper.GetValue(args, "--login-config") ?? "conf/login_athena.conf";
        var interConfigPath = ArgsHelper.GetValue(args, "--inter-config") ?? "conf/inter_athena.conf";
        var secretsPath = ArgsHelper.GetValue(args, "--secrets") ?? "solutionfiles/secrets/secret.json";
        var subnetConfigPath = ArgsHelper.GetValue(args, "--subnet-config") ?? "conf/subnet_athena.conf";
        var selfTest = ArgsHelper.HasFlag(args, "--self-test");
        var autoMigrate = ArgsHelper.HasFlag(args, "--auto-migrate") ||
            string.Equals(Environment.GetEnvironmentVariable("ATHENA_NET_LOGIN_DB_AUTOMIGRATE"), "true", StringComparison.OrdinalIgnoreCase);
        var config = LoginConfigLoader.Load(configPath);
        LoginLogger.Configure(config);
        var configStore = new LoginConfigStore(config, configPath);
        var interConfig = InterConfigLoader.Load(interConfigPath);
        var secrets = SecretConfig.Load(secretsPath);
        var subnetConfig = SubnetConfigLoader.Load(subnetConfigPath);
        var loginMsgPath = ArgsHelper.GetValue(args, "--login-msg-config") ?? "conf/msg_conf/login_msg.conf";
        var loginMessages = new LoginMessageStore(LoginMessageLoader.Load(loginMsgPath), loginMsgPath);
        var tableNames = new LoginDbTableNames
        {
            AccountTable = interConfig.LoginAccountTable,
            IpBanTable = interConfig.IpBanTable,
            LoginLogTable = interConfig.LoginLogTable,
            GlobalAccRegNumTable = interConfig.GlobalAccRegNumTable,
            GlobalAccRegStrTable = interConfig.GlobalAccRegStrTable,
        };
        var charServers = new CharServerRegistry();
        var state = new LoginState();
        Func<LoginDbContext?> dbFactory = () => null;

        LoginLogger.Status($"Login server starting on {config.BindIp}:{config.LoginPort} (PACKETVER 20220406)");

        var connectionString = ResolveConnectionString(interConfig, secrets);
        var dbProvider = ResolveDbProvider(interConfig, secrets, connectionString);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                var optionsBuilder = new DbContextOptionsBuilder<LoginDbContext>();
                if (dbProvider == "sqlserver")
                {
                    optionsBuilder.UseSqlServer(connectionString);
                }
                else if (dbProvider == "mysql")
                {
                    optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
                }
                else
                {
                    LoginLogger.Error($"DB: unsupported provider '{dbProvider}'.");
                    return 1;
                }

                optionsBuilder.ConfigureWarnings(warnings =>
                    warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

                var options = optionsBuilder.Options;

                dbFactory = () => new LoginDbContext(options, tableNames);

                var dbInstance = dbFactory();
                if (dbInstance != null)
                {
                    await using var db = dbInstance;
                    if (autoMigrate)
                    {
                        var hasMigrations = db.Database.GetMigrations().Any();
                        if (hasMigrations)
                        {
                            await db.Database.MigrateAsync();
                            LoginLogger.Status("DB: migrations applied.");
                        }
                        else
                        {
                            await db.Database.EnsureCreatedAsync();
                            LoginLogger.Status("DB: schema created (EnsureCreated).");
                        }
                    }
                    var canConnect = await db.Database.CanConnectAsync();
                    LoginLogger.Status(canConnect ? "DB: connected." : "DB: unable to connect.");
                }
                else
                {
                    LoginLogger.Error("DB: unable to create connection.");
                }
            }
            catch (Exception ex)
            {
                LoginLogger.Error($"DB: connection check failed ({ex.Message}).");
            }
        }
        else
        {
            LoginLogger.Error("DB: no connection string configured (check conf/inter_athena.conf).");
        }

        if (selfTest)
        {
            var exitCode = await SelfTest.RunAsync(config, dbFactory);
            return exitCode;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        state.OnAutoDisconnect = accountId => DisableWebAuthTokenAsync(accountId, configStore, state, dbFactory, cts.Token);

        var consoleTask = StartConsoleAsync(configStore, loginMessages, charServers, state, dbFactory, cts);
        var server = new LoginTcpServer(configStore, loginMessages, dbFactory, charServers, state, subnetConfig);
        var cleanupTask = StartIpBanCleanupAsync(configStore, dbFactory, cts.Token);
        var ipSyncTask = StartIpSyncAsync(configStore, charServers, cts.Token);
        var onlineCleanupTask = StartOnlineCleanupAsync(state, cts.Token);
        await server.RunAsync(cts.Token);
        await cleanupTask;
        await ipSyncTask;
        await onlineCleanupTask;
        await consoleTask;

        return 0;
    }

    private static Task StartIpBanCleanupAsync(LoginConfigStore configStore, Func<LoginDbContext?> dbFactory, CancellationToken cancellationToken)
    {
        if (!configStore.Current.IpBanEnabled || configStore.Current.IpBanCleanupIntervalSeconds <= 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var interval = configStore.Current.IpBanCleanupIntervalSeconds;
                    if (interval <= 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
                    var db = dbFactory();
                    if (db == null)
                    {
                        continue;
                    }

                    await using (db)
                    {
                        var now = DateTime.UtcNow;
                        var affected = await db.IpBanList
                            .Where(entry => entry.ReleaseTime <= now)
                            .ExecuteDeleteAsync(cancellationToken);
                        if (affected > 0)
                        {
                        LoginLogger.Info($"IPBan cleanup removed {affected} rows.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LoginLogger.Warning($"IPBan cleanup error: {ex.Message}");
                }
            }
        }, cancellationToken);
    }

    private static string ResolveConnectionString(InterConfig interConfig, SecretConfig secrets)
    {
        var envConnection = Environment.GetEnvironmentVariable("ATHENA_NET_LOGIN_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(envConnection))
        {
            return envConnection;
        }

        if (!string.IsNullOrWhiteSpace(secrets.LoginDbConnectionString))
        {
            return secrets.LoginDbConnectionString;
        }

        return interConfig.LoginDbConnectionString;
    }

    private static string ResolveDbProvider(InterConfig interConfig, SecretConfig secrets, string connectionString)
    {
        var envProvider = Environment.GetEnvironmentVariable("ATHENA_NET_LOGIN_DB_PROVIDER");
        if (!string.IsNullOrWhiteSpace(envProvider))
        {
            return envProvider.Trim().ToLowerInvariant();
        }

        var provider = !string.IsNullOrWhiteSpace(secrets.LoginDbProvider)
            ? secrets.LoginDbProvider
            : interConfig.LoginDbProvider;

        if (!string.IsNullOrWhiteSpace(provider))
        {
            return provider.Trim().ToLowerInvariant();
        }

        return GuessDbProvider(connectionString);
    }

    private static string GuessDbProvider(string connectionString)
    {
        if (connectionString.Contains("Port=", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("SslMode=", StringComparison.OrdinalIgnoreCase))
        {
            return "mysql";
        }

        if (connectionString.Contains("TrustServerCertificate=", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("Encrypt=", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("User ID=", StringComparison.OrdinalIgnoreCase))
        {
            return "sqlserver";
        }

        return string.Empty;
    }

    private static Task StartConsoleAsync(LoginConfigStore configStore, LoginMessageStore loginMessages, CharServerRegistry charServers, LoginState state, Func<LoginDbContext?> dbFactory, CancellationTokenSource cts)
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
                        var ok = configStore.Reload();
                        if (ok)
                        {
                            LoginLogger.Configure(configStore.Current);
                        }

                        var msgOk = loginMessages.Reload();
                        LoginLogger.Status(ok && msgOk ? "Config reloaded." : "Config reload failed.");
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
                    var ok = configStore.Reload();
                    if (ok)
                    {
                        LoginLogger.Configure(configStore.Current);
                    }

                    var msgOk = loginMessages.Reload();
                    LoginLogger.Status(ok && msgOk ? "Config reloaded." : "Config reload failed.");
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
            var exists = await db.Accounts.AsNoTracking().AnyAsync(a => a.UserId == user, cancellationToken);
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

    private static Task StartIpSyncAsync(LoginConfigStore configStore, CharServerRegistry charServers, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var interval = configStore.Current.IpSyncIntervalMinutes;
                if (interval <= 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    continue;
                }

                await Task.Delay(TimeSpan.FromMinutes(interval), cancellationToken);

                var payload = new byte[2];
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(0, 2), PacketConstants.LcIpSyncRequest);
                await charServers.SendToAllAsync(payload);
            }
        }, cancellationToken);
    }

    private static Task StartOnlineCleanupAsync(LoginState state, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(600);
        return Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cancellationToken);
                    state.CleanupUnknownUsers(interval);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cancellationToken);
    }

    private static async Task DisableWebAuthTokenAsync(uint accountId, LoginConfigStore configStore, LoginState state, Func<LoginDbContext?> dbFactory, CancellationToken cancellationToken)
    {
        if (!configStore.Current.UseWebAuthToken)
        {
            return;
        }

        var delay = configStore.Current.DisableWebTokenDelayMs;
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (state.TryGetOnlineUser(accountId, out _))
        {
            return;
        }

        var db = dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            account.WebAuthTokenEnabled = false;
            db.Entry(account).Property(a => a.WebAuthTokenEnabled).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
        }
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

    private static class ArgsHelper
    {
        public static string? GetValue(string[] args, string key)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        public static bool HasFlag(string[] args, string flag)
        {
            return args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
        }
    }
}
