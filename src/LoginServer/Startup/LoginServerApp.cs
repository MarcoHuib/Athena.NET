using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Logging;
using Athena.Net.LoginServer.Net;
using Athena.Net.LoginServer.Runtime;
using Athena.Net.LoginServer.Telemetry;

namespace Athena.Net.LoginServer.Startup;

public static class LoginServerApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = StartupOptions.Parse(args);

        var config = LoginConfigLoader.Load(options.ConfigPath);
        LoginLogger.Configure(config);
        using var telemetry = LoginTelemetry.Start();
        var interConfig = InterConfigLoader.Load(options.InterConfigPath);
        var configStore = new LoginConfigStore(config, options.ConfigPath, interConfig.LoginCaseSensitive);
        var secrets = SecretConfig.Load(options.SecretsPath);
        var subnetConfig = SubnetConfigLoader.Load(options.SubnetConfigPath);
        var loginMessages = new LoginMessageStore(LoginMessageLoader.Load(options.LoginMsgPath), options.LoginMsgPath);

        var tableNames = new LoginDbTableNames
        {
            AccountTable = interConfig.LoginAccountTable,
            IpBanTable = interConfig.IpBanTable,
            LoginLogTable = interConfig.LoginLogTable,
            GlobalAccRegNumTable = interConfig.GlobalAccRegNumTable,
            GlobalAccRegStrTable = interConfig.GlobalAccRegStrTable,
        };

        LoginLogger.Status($"Login server starting on {config.BindIp}:{config.LoginPort} (PACKETVER 20220406)");

        var composition = ServiceComposition.Build(interConfig, secrets, tableNames, configStore);
        await using var serviceProvider = composition.Provider;
        var dbAvailable = composition.DbAvailable;
        var dbFactory = dbAvailable
            ? await DbSetup.CreateDbFactoryAsync(serviceProvider, options.AutoMigrate)
            : (Func<LoginDbContext?>)(() => null);

        var identityDbFactory = composition.IdentityDbAvailable
            ? await IdentityDbSetup.CreateDbFactoryAsync(serviceProvider, options.AutoMigrate)
            : (Func<AthenaIdentityDbContext?>)(() => null);

        if (options.SelfTest)
        {
            var exitCode = await SelfTest.RunAsync(config, dbFactory, serviceProvider);
            return exitCode;
        }

        if (options.CreateAccountUserName != null)
        {
            return await CreateAccountAsync(options, serviceProvider);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var charServers = new CharServerRegistry();
        var state = new LoginState();
        state.OnAutoDisconnect = accountId => BackgroundTasks.DisableWebAuthTokenAsync(accountId, configStore, state, dbFactory, cts.Token);

        var consoleTask = ConsoleCommandLoop.StartAsync(configStore, loginMessages, options.InterConfigPath, charServers, state, dbFactory, serviceProvider, cts);
        var server = new LoginTcpServer(configStore, loginMessages, dbFactory, identityDbFactory, charServers, state, subnetConfig, serviceProvider);
        var cleanupTask = BackgroundTasks.StartIpBanCleanupAsync(configStore, dbFactory, cts.Token);
        var ipSyncTask = BackgroundTasks.StartIpSyncAsync(configStore, charServers, cts.Token);
        var onlineCleanupTask = BackgroundTasks.StartOnlineCleanupAsync(state, cts.Token);

        await server.RunAsync(cts.Token);
        await cleanupTask;
        await ipSyncTask;
        await onlineCleanupTask;
        await consoleTask;

        return 0;
    }

    private static async Task<int> CreateAccountAsync(StartupOptions options, IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(options.CreateAccountUserName) || string.IsNullOrWhiteSpace(options.CreateAccountPassword))
        {
            LoginLogger.Error("--create-account-username and --create-account-password are required.");
            return 1;
        }

        using var scope = serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetService<IPlayerAccountProvisioningService>();
        if (provisioning == null)
        {
            LoginLogger.Error("Identity DB: unavailable, cannot create a player account.");
            return 1;
        }

        var email = options.CreateAccountEmail ?? $"{options.CreateAccountUserName}@players.athena.local";
        var result = await provisioning.ProvisionAsync(options.CreateAccountUserName, email, options.CreateAccountPassword, options.CreateAccountSex, CancellationToken.None);
        if (!result.Success)
        {
            LoginLogger.Error($"Account '{options.CreateAccountUserName}' was not created: {result.ErrorMessage}");
            return 1;
        }

        LoginLogger.Status($"Account '{options.CreateAccountUserName}' created (RagnarokAccountId={result.RagnarokAccountId}).");
        return 0;
    }
}
