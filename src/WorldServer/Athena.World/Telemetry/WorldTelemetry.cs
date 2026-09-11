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
    public static readonly Counter<long> ActorIdBlockLeases = Meter.CreateCounter<long>("world.actorid.block.leases");
    // Phase 2B monster-tick cadence/duration telemetry - the concrete, evidence-backed response to
    // the accepted movement-jump investigation finding (the old MapServer-local tick loop never
    // measured actual elapsed time between ticks at all). ElapsedSinceLast surfaces exactly the
    // condition (a late tick) that produced the investigated client-visible symptom, without this
    // phase claiming to have eliminated the symptom itself - see WorldPartitionGrain's own tick
    // doc comment.
    public static readonly Histogram<double> MonsterTickElapsedSinceLast = Meter.CreateHistogram<double>("world.monster.tick.elapsed_since_last", "ms");
    public static readonly Histogram<double> MonsterTickProcessingDuration = Meter.CreateHistogram<double>("world.monster.tick.duration", "ms");
    public static readonly Counter<long> MonsterTickLate = Meter.CreateCounter<long>("world.monster.tick.late");

    public static IServiceCollection AddWorldTelemetry(this IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing => tracing.AddSource("Microsoft.Orleans.Runtime").AddOtlpExporter())
            .WithMetrics(metrics => metrics.AddMeter(ServiceName, "Microsoft.Orleans").AddOtlpExporter());
        return services;
    }
}
