using Athena.Net.World.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans();
builder.Services.AddWorldTelemetry();
var topologyPath = Environment.GetEnvironmentVariable("ATHENA_WORLD_PARTITIONS_PATH")
    ?? throw new InvalidOperationException("ATHENA_WORLD_PARTITIONS_PATH is required and must identify the shared World topology file.");
var collisionPath = Environment.GetEnvironmentVariable("ATHENA_WORLD_MAP_CACHE_PATH")
    ?? throw new InvalidOperationException("ATHENA_WORLD_MAP_CACHE_PATH is required and must identify the World collision source.");
var resolver = WorldPartitionTopologyLoader.Load(topologyPath, []);
var collisionRuntime = WorldCollisionRuntimeLoader.LoadMapCache(collisionPath);
Console.WriteLine($"World partition topology loaded: {Path.GetFullPath(topologyPath)}");
Console.WriteLine($"World collision source loaded: map_cache.dat {collisionRuntime.SourcePath}");
Console.WriteLine($"World collision maps loaded: {collisionRuntime.MapCount}");
builder.Services.AddSingleton<IWorldPartitionResolver>(resolver);
builder.Services.AddSingleton<IMapCollisionProvider>(collisionRuntime.Collision);
builder.Services.AddSingleton<IMovementPathProvider>(collisionRuntime.Movement);

await builder.Build().RunAsync();
