using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Athena.Net.LoginServer.Telemetry;

public sealed class LoginTelemetry : IDisposable
{
    private const string ServiceName = "login-server";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);
    public static readonly Counter<long> ConnectionsAccepted = Meter.CreateCounter<long>("login.connections.accepted");

    private readonly TracerProvider _tracerProvider;
    private readonly MeterProvider _meterProvider;
    private readonly ILoggerFactory _loggerFactory;

    private LoginTelemetry(TracerProvider tracerProvider, MeterProvider meterProvider, ILoggerFactory loggerFactory)
    {
        _tracerProvider = tracerProvider;
        _meterProvider = meterProvider;
        _loggerFactory = loggerFactory;
    }

    public static LoginTelemetry Start()
    {
        var resource = ResourceBuilder.CreateDefault().AddService(ServiceName);
        var startTime = DateTimeOffset.UtcNow;

        var endpoint = ResolveOtlpEndpoint();
        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(ServiceName)
            .AddOtlpExporter(options => ApplyEndpoint(options, endpoint))
            .Build();

        Meter.CreateObservableGauge("login.uptime.seconds", () => (DateTimeOffset.UtcNow - startTime).TotalSeconds);
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
        Logging.LoginLogger.SetTelemetrySink((level, message) =>
        {
            otelLogger.Log(MapLogLevel(level), message);
        });

        using (var activity = ActivitySource.StartActivity("login.startup"))
        {
            activity?.SetTag("startup.utc", startTime.ToString("O"));
        }

        return new LoginTelemetry(tracerProvider, meterProvider, loggerFactory);
    }

    private static LogLevel MapLogLevel(Logging.LogLevel level)
    {
        return level switch
        {
            Logging.LogLevel.Debug => LogLevel.Debug,
            Logging.LogLevel.Info => LogLevel.Information,
            Logging.LogLevel.Status => LogLevel.Information,
            Logging.LogLevel.Notice => LogLevel.Information,
            Logging.LogLevel.Warning => LogLevel.Warning,
            Logging.LogLevel.Error => LogLevel.Error,
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
        Logging.LoginLogger.SetTelemetrySink(null);
        _loggerFactory.Dispose();
        _meterProvider.Dispose();
        _tracerProvider.Dispose();
    }
}
