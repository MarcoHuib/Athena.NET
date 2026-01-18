using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Athena.Net.MapServer.Telemetry;

public sealed class MapTelemetry : IDisposable
{
    private const string ServiceName = "map-server";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);
    public static readonly Counter<long> ConnectionsAccepted = Meter.CreateCounter<long>("map.connections.accepted");

    private readonly TracerProvider _tracerProvider;
    private readonly MeterProvider _meterProvider;
    private readonly ILoggerFactory _loggerFactory;

    private MapTelemetry(TracerProvider tracerProvider, MeterProvider meterProvider, ILoggerFactory loggerFactory)
    {
        _tracerProvider = tracerProvider;
        _meterProvider = meterProvider;
        _loggerFactory = loggerFactory;
    }

    public static MapTelemetry Start()
    {
        var resource = ResourceBuilder.CreateDefault().AddService(ServiceName);
        var startTime = DateTimeOffset.UtcNow;

        var endpoint = ResolveOtlpEndpoint();
        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(ServiceName)
            .AddOtlpExporter(options => ApplyEndpoint(options, endpoint))
            .Build();

        Meter.CreateObservableGauge("map.uptime.seconds", () => (DateTimeOffset.UtcNow - startTime).TotalSeconds);
        var meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(ServiceName)
            .AddOtlpExporter(options => ApplyEndpoint(options, endpoint))
            .Build();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(resource);
                options.AddOtlpExporter(exporter => ApplyEndpoint(exporter, endpoint));
            });
        });

        var otelLogger = loggerFactory.CreateLogger(ServiceName);
        Logging.MapLogger.SetTelemetrySink((level, message) =>
        {
            otelLogger.Log(MapLogLevel(level), message);
        });

        using (var activity = ActivitySource.StartActivity("map.startup"))
        {
            activity?.SetTag("startup.utc", startTime.ToString("O"));
        }

        return new MapTelemetry(tracerProvider, meterProvider, loggerFactory);
    }

    private static LogLevel MapLogLevel(string level)
    {
        return level switch
        {
            "ERROR" => LogLevel.Error,
            "WARN" => LogLevel.Warning,
            "INFO" => LogLevel.Information,
            "STATUS" => LogLevel.Information,
            _ => LogLevel.Information,
        };
    }

    private static Uri? ResolveOtlpEndpoint()
    {
        var endpoint = Environment.GetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_ENDPOINT_URL");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static void ApplyEndpoint(OtlpExporterOptions options, Uri? endpoint)
    {
        if (endpoint != null)
        {
            options.Endpoint = endpoint;
        }

        options.Protocol = OtlpExportProtocol.Grpc;
    }

    public void Dispose()
    {
        Logging.MapLogger.SetTelemetrySink(null);
        _loggerFactory.Dispose();
        _meterProvider.Dispose();
        _tracerProvider.Dispose();
    }
}
