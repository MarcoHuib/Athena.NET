using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Telemetry;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

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
        // Fails startup loudly (does not fall back to EmptyMapCollisionProvider) if the configured
        // map_cache_path/map_collision_artifact source is missing/malformed/duplicated - see
        // MapCollisionStartupLoader's own doc comment. An unconfigured server (neither key set) is
        // unaffected: Load returns EmptyMapCollisionProvider.Instance, the same default
        // MapServerWorld.Build already used.
        var collisionProvider = MapCollisionStartupLoader.Load(mergedConfig.CollisionArtifacts, mergedConfig.MapCachePath);
        // Production-only fail-closed guard (never applied inside MapServerWorld.Build itself, so
        // tests can still freely compose a collision-less world on purpose) - see that method's own
        // doc comment. A live MapServer with generated monster spawns and no real collision source
        // must refuse to start rather than silently place monsters on
        // UnverifiedFallbackMobSpawnCellSelector's fabricated deterministic raster.
        MapServerWorld.RequireRealCollisionSourceIfMobSpawnsExist(GeneratedScriptRegistry.MobSpawns.Count > 0, collisionProvider);
        MapLogger.Status(
            $"Monster spawn positioning: {(ReferenceEquals(collisionProvider, EmptyMapCollisionProvider.Instance) ? "none configured (no generated monster spawns)" : "rAthena collision-backed")}");
        var world = MapServerWorld.Build(gameplayRules, collisionProvider: collisionProvider);
        var connector = new CharServerConnector(configStore);
        var mapServer = new MapTcpServer(configStore, connector, world);

        var connectTask = connector.RunAsync(cts.Token);
        await mapServer.RunAsync(cts.Token);
        await connectTask;

        return 0;
    }
}
