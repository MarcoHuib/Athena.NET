using Athena.Net.World.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(siloBuilder =>
{
    // Memory-backed grain storage for ActorIdBlockAuthorityGrain's persisted cursor - survives
    // ordinary activation deactivation/reactivation within this running silo process, explicitly
    // NOT durable across a full silo restart. See ActorIdBlockAuthorityGrain's own doc comment for
    // the full invariant this is meant to provide and its stated limitation.
    siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
});
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
// Phase 2B monster simulation (MonsterRegistry/MobInstance, file-linked into Athena.World.Monsters
// - see WorldMonsterMapSimulation's own doc comment) needs a TimeProvider exactly like MapServer's
// own equivalent composition root already supplies one.
builder.Services.AddSingleton(TimeProvider.System);

await builder.Build().RunAsync();
