using Athena.Net.World.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans();
builder.Services.AddWorldTelemetry();

await builder.Build().RunAsync();
