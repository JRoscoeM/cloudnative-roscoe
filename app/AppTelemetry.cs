using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace App;

public static class AppTelemetry
{
    /// <summary>Start custom spans here, such as one around each OpenAI call.</summary>
    public static readonly ActivitySource Source = new("App");

    private static readonly Meter Meter = new("App");

    /// <summary>Readings saved, by source: sensor or snapshot.</summary>
    public static readonly Counter<long> Readings = Meter.CreateCounter<long>("headroom.readings");

    /// <summary>Photos analyzed, by outcome: counted, low_confidence or failed.</summary>
    public static readonly Counter<long> Snapshots = Meter.CreateCounter<long>("headroom.snapshots");

    public static bool ExportConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    /// <summary>
    /// Traces, logs and metrics go to Dash0 over OTLP, using the standard OTEL_*
    /// environment variables. Metrics are also exposed on /metrics for Prometheus.
    /// </summary>
    public static void AddAppTelemetry(this WebApplicationBuilder builder, AppInfo info)
    {
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: "app", serviceVersion: info.Version))
            .WithTracing(tracing => tracing
                .AddSource(Source.Name)
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !IsOperationalPath(context.Request.Path))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(Meter.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddPrometheusExporter())
            .WithLogging();

        if (ExportConfigured(builder.Configuration))
        {
            telemetry.UseOtlpExporter();
        }
    }

    private static bool IsOperationalPath(PathString path) =>
        path.StartsWithSegments("/healthz") || path.StartsWithSegments("/readyz") || path.StartsWithSegments("/metrics");
}
