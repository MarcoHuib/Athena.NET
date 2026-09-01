using Athena.Net.World.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Athena.Net.World.Contracts;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans();
builder.Services.AddWorldTelemetry();
var topologyPath = Environment.GetEnvironmentVariable("ATHENA_WORLD_PARTITIONS_PATH") ?? Path.Combine("conf", "world_partitions.json");
builder.Services.AddSingleton<IWorldPartitionResolver>(_ => WorldPartitionTopologyLoader.Load(topologyPath, []));

await builder.Build().RunAsync();
