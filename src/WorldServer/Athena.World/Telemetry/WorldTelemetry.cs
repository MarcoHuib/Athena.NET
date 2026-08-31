using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Athena.Net.World.Telemetry;

public static class WorldTelemetry
{
    public const string ServiceName = "athena-world";
    public static readonly Meter Meter = new(ServiceName);
    public static readonly Counter<long> PartitionActivations = Meter.CreateCounter<long>("world.partition.activation");
    public static readonly Histogram<double> PartitionCommandDuration = Meter.CreateHistogram<double>("world.partition.command.duration", "ms");
    public static readonly Histogram<double> PartitionTransferDuration = Meter.CreateHistogram<double>("world.partition.transfer.duration", "ms");
    public static readonly Counter<long> PartitionTransferFailures = Meter.CreateCounter<long>("world.partition.transfer.failures");

    public static IServiceCollection AddWorldTelemetry(this IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing => tracing.AddSource("Microsoft.Orleans.Runtime").AddOtlpExporter())
            .WithMetrics(metrics => metrics.AddMeter(ServiceName, "Microsoft.Orleans").AddOtlpExporter());
        return services;
    }
}
