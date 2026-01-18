using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Logging;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Startup;

public static class CharServerApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = StartupOptions.Parse(args);

        var config = CharConfigLoader.Load(options.ConfigPath);
        var interConfig = InterConfigLoader.Load(options.InterConfigPath);
        var secrets = SecretConfig.Load(options.SecretsPath);
        var mergedConfig = secrets.ApplyTo(config);
        CharLogger.Configure(mergedConfig);
        var configStore = new CharConfigStore(mergedConfig, options.ConfigPath);
        var tableNames = new Db.CharDbTableNames
        {
            CharTable = interConfig.CharTable,
            InventoryTable = interConfig.InventoryTable,
            SkillTable = interConfig.SkillTable,
            HotkeyTable = interConfig.HotkeyTable,
        };
        var dbFactory = Runtime.DbSetup.Configure(interConfig, secrets, tableNames, options.AutoMigrate);

        CharLogger.Status($"Char server starting (PACKETVER {PacketConstants.PacketVer})");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var loginConnector = new LoginServerConnector(configStore);
        var charServer = new CharTcpServer(configStore, loginConnector, dbFactory, interConfig.StartStatusPoints);

        var loginTask = loginConnector.RunAsync(cts.Token);
        await charServer.RunAsync(cts.Token);
        await loginTask;

        return 0;
    }
}
