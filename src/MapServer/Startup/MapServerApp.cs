using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Telemetry;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

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

        MapLogger.Status($"Map server starting (PACKETVER {PacketConstants.PacketVer}, build={BuildRevision.Current})");

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
        // `--map-cache-path` wins over the configured `map_cache_path` value. Either value is an
        // explicit legacy/debug override; with neither set, startup opens the generated production
        // Athena Map Pack copied next to MapServer by build/publish.
        var effectiveMapCachePath = options.MapCachePathOverride ?? mergedConfig.MapCachePath;
        // Fails startup loudly if the generated pack or configured override is missing/malformed.
        // map_cache_path/map_collision_artifact source is missing/malformed/duplicated - see
        // MapCollisionStartupLoader's own doc comment. An unconfigured server (neither key set) is
        // An unconfigured server uses GeneratedMapCollisionProvider.
        // ruleSet: MapCollisionStartupLoader merges pinned rAthena's own db/import/map_cache.dat
        // and ruleset-specific overlay (db/re/map_cache.dat for Renewal) over the configured
        // generic map_cache_path, matching pinned map_readallmaps' own three-layer load order
        // exactly - see that loader's own doc comment for why this is required (real example:
        // pinned "prontera" geometry exists ONLY in db/re/map_cache.dat, not the generic
        // db/map_cache.dat this project was previously loading alone).
        var collisionProvider = MapCollisionStartupLoader.Load(mergedConfig.CollisionArtifacts, effectiveMapCachePath, gameplayOptions.RuleSet);
        using var collisionProviderLifetime = collisionProvider as IDisposable;
        // Production-only fail-closed guard (never applied inside MapServerWorld.Build itself, so
        // tests can still freely compose a collision-less world on purpose) - see that method's own
        // doc comment. A live MapServer with generated monster spawns and no real collision source
        // must refuse to start rather than silently place monsters on
        // UnverifiedFallbackMobSpawnCellSelector's fabricated deterministic raster.
        MapServerWorld.RequireRealCollisionSourceIfMobSpawnsExist(GeneratedScriptRegistry.MobSpawns.Count > 0, collisionProvider);
        // Broader than the mob-spawn guard above: a served map with NO monster spawns at all
        // (e.g. "prontera") still needs real collision data for ordinary player movement - see
        // MapServerHostingScope.RequireCollisionForAllServedMaps's own doc comment for the live
        // crash this specifically catches (reproduced on head 57dc569: auth+bootstrap succeed,
        // first player movement request throws with no collision data loaded).
        MapServerHostingScope.RequireCollisionForAllServedMaps(collisionProvider);
        MapLogger.Status(
            $"Monster spawn positioning: {(ReferenceEquals(collisionProvider, EmptyMapCollisionProvider.Instance) ? "none configured (no generated monster spawns)" : "collision-backed")}");
        MapLogger.Status($"Customs (handwritten Athena.NET development content): {(mergedConfig.CustomsEnabled ? "enabled" : "disabled")}");
        // Explicit runtime/deployment hosting scope for MapServerWorld.Build's servedMaps parameter
        // - see MapServerHostingScope's own doc comment for why this is a hand-declared set, never
        // derived from the warp graph or collision-data availability.
        var world = MapServerWorld.Build(gameplayRules, collisionProvider: collisionProvider, rates: mergedConfig.GameplayRates, customsEnabled: mergedConfig.CustomsEnabled, servedMaps: MapServerHostingScope.ServedMaps);
        var hostBuilder = Host.CreateApplicationBuilder(args);
        hostBuilder.UseOrleansClient();
        using var orleansHost = hostBuilder.Build();
        await orleansHost.StartAsync(cts.Token);
        var worldRuntime = new OrleansWorldRuntime(orleansHost.Services.GetRequiredService<IClusterClient>());
        var connector = new CharServerConnector(configStore);
        var mapServer = new MapTcpServer(configStore, connector, world, worldRuntime);

        var connectTask = connector.RunAsync(cts.Token);
        await mapServer.RunAsync(cts.Token);
        await connectTask;
        await orleansHost.StopAsync();

        return 0;
    }
}
