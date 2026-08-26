using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Telemetry;
using Athena.Net.MapServer.World;

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

        // Gameplay.RuleSet is selected ONCE here, at the composition root - see
        // GameplayRulesFactory's own doc comment for why an unsupported ruleset must
        // fail startup loudly instead of being silently downgraded to Renewal.
        // MapServerWorld.Build receives the already-composed GameplayRuleServices
        // bundle and never itself inspects GameplayOptions/RagnarokRuleSet or calls
        // GameplayRulesFactory - this is the one and only place that decision is made.
        var gameplayOptions = new GameplayOptions { RuleSet = mergedConfig.GameplayRuleSet };
        MapLogger.Status($"Gameplay ruleset: {gameplayOptions.RuleSet}");
        var gameplayRules = GameplayRulesFactory.Create(gameplayOptions);
        var world = MapServerWorld.Build(gameplayRules);
        var connector = new CharServerConnector(configStore);
        var mapServer = new MapTcpServer(configStore, connector, world);

        var connectTask = connector.RunAsync(cts.Token);
        await mapServer.RunAsync(cts.Token);
        await connectTask;

        return 0;
    }
}
