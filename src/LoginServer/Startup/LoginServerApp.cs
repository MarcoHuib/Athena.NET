using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
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

        var dbFactory = DbSetup.Configure(interConfig, secrets, tableNames, options.AutoMigrate);

        if (options.SelfTest)
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

        var charServers = new CharServerRegistry();
        var state = new LoginState();
        state.OnAutoDisconnect = accountId => BackgroundTasks.DisableWebAuthTokenAsync(accountId, configStore, state, dbFactory, cts.Token);

        var consoleTask = ConsoleCommandLoop.StartAsync(configStore, loginMessages, options.InterConfigPath, charServers, state, dbFactory, cts);
        var server = new LoginTcpServer(configStore, loginMessages, dbFactory, charServers, state, subnetConfig);
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
}
