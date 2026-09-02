using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Telemetry;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;
using Athena.Net.World.Contracts;
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

        var configStore = new MapConfigStore(
            mergedConfig,
            options.ConfigPath);

        MapLogger.Status(
            $"Map server starting " +
            $"(PACKETVER {PacketConstants.PacketVer}, " +
            $"build={BuildRevision.Current})");

        //
        // Application cancellation
        //
        // Do NOT use Console.CancelKeyPress here.
        //
        // Under Aspire/DCP on macOS, registering Console.CancelKeyPress
        // caused a ~17 second startup delay.
        //
        // Cancellation is instead connected to the Generic Host's
        // IHostApplicationLifetime below. The host handles SIGINT/SIGTERM
        // and Aspire shutdown, after which this token is cancelled.
        //
        using var cts = new CancellationTokenSource();

        //
        // Gameplay rules
        //
        // Gameplay.RuleSet is selected exactly once here at the
        // composition root.
        //
        var gameplayOptions = new GameplayOptions
        {
            RuleSet = mergedConfig.GameplayRuleSet
        };

        MapLogger.Status(
            $"Gameplay ruleset: {gameplayOptions.RuleSet}");

        var gameplayRules =
            GameplayRulesFactory.Create(gameplayOptions);

        //
        // Collision data
        //
        // --map-cache-path wins over the configured map_cache_path.
        //
        // Either value is an explicit legacy/debug override.
        // Without either override, startup uses the generated
        // production Athena Map Pack.
        //
        var effectiveMapCachePath =
            options.MapCachePathOverride ??
            mergedConfig.MapCachePath;

        var collisionProvider =
            MapCollisionStartupLoader.Load(
                mergedConfig.CollisionArtifacts,
                effectiveMapCachePath,
                gameplayOptions.RuleSet);

        using var collisionProviderLifetime =
            collisionProvider as IDisposable;

        //
        // Production fail-closed validation.
        //
        // A live MapServer with generated monster spawns must have
        // real collision data available.
        //
        MapServerWorld.RequireRealCollisionSourceIfMobSpawnsExist(
            GeneratedScriptRegistry.MobSpawns.Count > 0,
            collisionProvider);

        //
        // All served maps require real collision data for ordinary
        // player movement, even if a map contains no monster spawns.
        //
        MapServerHostingScope.RequireCollisionForAllServedMaps(
            collisionProvider);

        MapLogger.Status(
            $"Monster spawn positioning: " +
            $"{(ReferenceEquals(
                collisionProvider,
                EmptyMapCollisionProvider.Instance)
                ? "none configured (no generated monster spawns)"
                : "collision-backed")}");

        MapLogger.Status(
            $"Customs (handwritten Athena.NET development content): " +
            $"{(mergedConfig.CustomsEnabled
                ? "enabled"
                : "disabled")}");

        //
        // Compose the local MapServer world.
        //
        // The served-map set is an explicit deployment/runtime scope
        // and is not inferred from the warp graph or collision data.
        //
        var world = MapServerWorld.Build(
            gameplayRules,
            collisionProvider: collisionProvider,
            rates: mergedConfig.GameplayRates,
            customsEnabled: mergedConfig.CustomsEnabled,
            servedMaps: MapServerHostingScope.ServedMaps,
            mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);

        var partitionTopologyPath =
            Environment.GetEnvironmentVariable("ATHENA_WORLD_PARTITIONS_PATH") ??
            Path.Combine("conf", "world_partitions.json");

        var partitionResolver =
            WorldPartitionTopologyLoader.Load(
                partitionTopologyPath,
                MapServerHostingScope.ServedMaps);

        WorldPartitionActorRanges.Validate(
            WorldPartitionActorRanges.Development);

        //
        // Orleans client host
        //
        var hostBuilder =
            Host.CreateApplicationBuilder(args);

        hostBuilder.UseOrleansClient();

        using var orleansHost =
            hostBuilder.Build();

        //
        // Let Microsoft.Extensions.Hosting own process lifetime.
        //
        // Aspire shutdown, SIGTERM and Ctrl+C are handled by the
        // Generic Host's ConsoleLifetime.
        //
        // When the host starts stopping, propagate that cancellation
        // to MapServer's own long-running loops.
        //
        var applicationLifetime =
            orleansHost.Services
                .GetRequiredService<IHostApplicationLifetime>();

        using var stoppingRegistration =
            applicationLifetime.ApplicationStopping.Register(
                cts.Cancel);

        await orleansHost.StartAsync(cts.Token);

        var worldRuntime =
            new OrleansWorldRuntime(
                orleansHost.Services
                    .GetRequiredService<IClusterClient>(),
                partitionResolver);

        var connector =
            new CharServerConnector(configStore);

        var mapServer =
            new MapTcpServer(
                configStore,
                connector,
                world,
                worldRuntime);

        try
        {
            var connectTask =
                connector.RunAsync(cts.Token);

            await connector.WaitUntilReadyAsync(cts.Token);

            await mapServer.RunAsync(cts.Token);

            await connectTask;
        }
        catch (OperationCanceledException)
            when (cts.IsCancellationRequested)
        {
            // Expected during normal Aspire shutdown, Ctrl+C or SIGTERM.
        }
        finally
        {
            await orleansHost.StopAsync(
                CancellationToken.None);
        }

        return 0;
    }
}
