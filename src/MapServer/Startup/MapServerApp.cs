using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Telemetry;

namespace Athena.Net.MapServer.Startup;

public static class MapServerApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = StartupOptions.Parse(args);

        var config = MapConfigLoader.Load(options.ConfigPath);
        var secrets = SecretConfig.Load(options.SecretsPath);
        var mergedConfig = secrets.ApplyTo(config);
        MapLogger.Configure(mergedConfig);
        using var telemetry = MapTelemetry.Start();
        var configStore = new MapConfigStore(mergedConfig, options.ConfigPath);

        MapLogger.Status($"Map server starting (PACKETVER {PacketConstants.PacketVer})");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var connector = new CharServerConnector(configStore);
        var mapServer = new MapTcpServer(configStore, connector);

        var connectTask = connector.RunAsync(cts.Token);
        await mapServer.RunAsync(cts.Token);
        await connectTask;

        return 0;
    }
}
