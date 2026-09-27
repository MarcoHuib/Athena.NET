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
    // MapServer's NPC/warp actor population is bounded by generated world content and does not
    // grow via respawn the way monster IDs do (MonsterRegistry's own per-partition
    // LeasedBlockActorIdAllocator re-leases on exhaustion; this process leases exactly once at
    // startup). 1,000,000 is comfortably larger than any current or realistically foreseeable
    // generated NPC/warp actor count for one MapServer process.
    private const uint NpcWarpActorIdBlockSize = 1_000_000;

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
        // World-partition topology is pure map-ownership policy - it carries no actor-ID concept
        // at all (see WorldPartitionTopology.cs's own doc comment). Global actor-ID uniqueness is
        // guaranteed separately, by leasing non-overlapping blocks from the single
        // ActorIdBlockAuthorityGrain once the Orleans client is available below - see
        // LeasedBlockActorIdAllocator's own doc comment for why partition topology and actor-ID
        // capacity planning are deliberately independent.
        //
        var partitionTopologyPath =
            Environment.GetEnvironmentVariable("ATHENA_WORLD_PARTITIONS_PATH") ??
            Path.Combine("conf", "world_partitions.json");

        var partitionResolver =
            WorldPartitionTopologyLoader.Load(
                partitionTopologyPath,
                MapServerHostingScope.ServedMaps);

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

        var clusterClient =
            orleansHost.Services
                .GetRequiredService<IClusterClient>();

        var worldRuntime =
            new OrleansWorldRuntime(
                clusterClient,
                partitionResolver);

        //
        // MapServer's own NPC/warp actor-ID domain now leases a block from the same global
        // ActorIdBlockAuthorityGrain monster partitions use, rather than owning a
        // hardcoded/config-declared numeric range - see LeasedBlockActorIdAllocator's own doc
        // comment. "npc-warp" is diagnostic-only (WorldTelemetry tagging), never a range key.
        // WorldActorIdAllocator now enforces the leased block's own EndExclusive boundary (see its
        // own doc comment) - it throws rather than silently allocating past what was actually
        // granted, which is what makes the global non-overlap guarantee real instead of aspirational.
        // One block comfortably covers this process's entire NPC/warp actor population for the
        // process lifetime (this domain has no respawn-driven growth the way monster IDs do), so no
        // re-lease-on-exhaustion handling is needed here the way LeasedBlockActorIdAllocator
        // provides for callers that DO need it (Athena.World's own per-partition monster allocation).
        //
        var npcWarpActorIdBlock =
            await clusterClient
                .GetGrain<IActorIdBlockAuthorityGrain>(ActorIdBlockAuthorityGrainKey.WellKnownKey)
                .LeaseBlockAsync("npc-warp", NpcWarpActorIdBlockSize);

        var npcWarpActorIdAllocator =
            new WorldActorIdAllocator(npcWarpActorIdBlock.StartInclusive - 1L, (long)npcWarpActorIdBlock.EndExclusive);

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
            mobSpawnMaps: MapServerHostingScope.MobSpawnMaps,
            actorIdAllocator: npcWarpActorIdAllocator);

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
