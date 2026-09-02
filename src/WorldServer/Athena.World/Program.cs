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
var topologyDocument = WorldPartitionTopologyLoader.LoadDocument(topologyPath);
WorldPartitionActorRanges.ValidateAll(topologyDocument);
var resolver = new WorldPartitionResolver(topologyDocument.Partitions, []);
var collisionRuntime = WorldCollisionRuntimeLoader.LoadMapCache(collisionPath);
Console.WriteLine($"World partition topology loaded: {Path.GetFullPath(topologyPath)}");
Console.WriteLine($"World collision source loaded: map_cache.dat {collisionRuntime.SourcePath}");
Console.WriteLine($"World collision maps loaded: {collisionRuntime.MapCount}");
builder.Services.AddSingleton<IWorldPartitionResolver>(resolver);
// The full parsed topology document (partition actorIdRange entries + npcWarpActorIdRange) is
// registered separately from IWorldPartitionResolver so WorldPartitionGrain can look up its own
// partition's actor-ID range by GetPrimaryKeyString() - the resolver itself only answers
// map-to-partition ownership questions, never actor-ID range questions.
builder.Services.AddSingleton(topologyDocument);
builder.Services.AddSingleton<IMapCollisionProvider>(collisionRuntime.Collision);
builder.Services.AddSingleton<IMovementPathProvider>(collisionRuntime.Movement);

await builder.Build().RunAsync();
