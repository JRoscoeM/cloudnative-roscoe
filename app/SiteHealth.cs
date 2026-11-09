using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Caching.Memory;

namespace App;

public sealed record MetricPoint(long Time, double Value);

public sealed record DependencyHealth(string Name, string State, string Detail);

public sealed record SiteHealthReport(
    string Overall,
    bool MetricsAvailable,
    double? RequestsPerMinute,
    double? ErrorPercent,
    double? LatencyP95Ms,
    int? PodsReady,
    int? PodsDesired,
    double? RestartsLastHour,
    double? MemoryPercent,
    double? ReadingsPerMinute,
    IReadOnlyList<MetricPoint> RequestSeries,
    IReadOnlyList<MetricPoint> ErrorSeries,
    IReadOnlyList<MetricPoint> LatencySeries,
    IReadOnlyList<DependencyHealth> Dependencies);

/// <summary>Reads from the cluster's Prometheus. Fixed queries only, so no user input reaches it.</summary>
public sealed class PrometheusClient(HttpClient http)
{
    public async Task<double?> InstantAsync(string query, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"/api/v1/query?query={Uri.EscapeDataString(query)}", cancellationToken);
        var results = document.RootElement.GetProperty("data").GetProperty("result");
        return results.GetArrayLength() == 0 ? null : Number(results[0].GetProperty("value")[1]);
    }

    public async Task<IReadOnlyList<MetricPoint>> RangeAsync(string query, TimeSpan window, TimeSpan step, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var path = $"/api/v1/query_range?query={Uri.EscapeDataString(query)}&start={(now - window).ToUnixTimeSeconds()}&end={now.ToUnixTimeSeconds()}&step={(int)step.TotalSeconds}";
        using var document = await GetAsync(path, cancellationToken);
        var results = document.RootElement.GetProperty("data").GetProperty("result");
        if (results.GetArrayLength() == 0)
        {
            return [];
        }

        return results[0].GetProperty("values").EnumerateArray()
            .Select(pair => (Time: pair[0].GetDouble(), Value: Number(pair[1])))
            .Where(point => point.Value is not null)
            .Select(point => new MetricPoint((long)point.Time, point.Value!.Value))
            .ToList();
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    // Prometheus sends numbers as strings, and "NaN" when there's no traffic.
    private static double? Number(JsonElement element) =>
        double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
}

public sealed class SiteHealthService(
    PrometheusClient prometheus,
    Backends backends,
    SensorAuth sensors,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<SiteHealthService> logger)
{
    private const string CacheKey = "site-health";
    private const string Namespace = "team-roscoe";
    private const double DegradedErrorPercent = 5;

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChartWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ChartStep = TimeSpan.FromMinutes(1);

    private static readonly string Requests =
        $"http_server_request_duration_seconds_count{{namespace=\"{Namespace}\",http_route!~\"/healthz|/readyz|/metrics\"}}";

    private static readonly string Failures =
        $"http_server_request_duration_seconds_count{{namespace=\"{Namespace}\",http_route!~\"/healthz|/readyz|/metrics\",http_response_status_code=~\"5..\"}}";

    private static readonly string RequestRate = $"sum(rate({Requests}[5m])) * 60";

    // "or vector(0)": with no failures there's no 5xx series at all, which means 0%.
    private static readonly string ErrorRate =
        $"(sum(rate({Failures}[5m])) or vector(0)) / sum(rate({Requests}[5m])) * 100";

    private static readonly string Latency =
        $"histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{{namespace=\"{Namespace}\",http_route!~\"/healthz|/readyz|/metrics\"}}[5m]))) * 1000";

    private static readonly string PodsReadyQuery = $"sum(kube_pod_status_ready{{namespace=\"{Namespace}\",condition=\"true\",pod=~\"app-.*\"}})";
    private static readonly string PodsDesiredQuery = $"max(kube_deployment_spec_replicas{{namespace=\"{Namespace}\",deployment=\"app\"}})";
    private static readonly string Restarts = $"sum(increase(kube_pod_container_status_restarts_total{{namespace=\"{Namespace}\",container=\"app\"}}[1h]))";

    private static readonly string Memory =
        $"max(container_memory_working_set_bytes{{namespace=\"{Namespace}\",container=\"app\"}}) / max(kube_pod_container_resource_limits{{namespace=\"{Namespace}\",container=\"app\",resource=\"memory\"}}) * 100";

    private static readonly string ReadingRate = $"sum(rate(headroom_readings_total{{namespace=\"{Namespace}\"}}[5m])) * 60";

    public async Task<SiteHealthReport> GetAsync(CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            return BuildAsync(cancellationToken);
        }))!;

    private async Task<SiteHealthReport> BuildAsync(CancellationToken cancellationToken)
    {
        var dependencies = await DependenciesAsync(cancellationToken);
        var now = time.GetUtcNow();

        try
        {
            var requests = prometheus.InstantAsync(RequestRate, cancellationToken);
            var errors = prometheus.InstantAsync(ErrorRate, cancellationToken);
            var latency = prometheus.InstantAsync(Latency, cancellationToken);
            var ready = prometheus.InstantAsync(PodsReadyQuery, cancellationToken);
            var desired = prometheus.InstantAsync(PodsDesiredQuery, cancellationToken);
            var restarts = prometheus.InstantAsync(Restarts, cancellationToken);
            var memory = prometheus.InstantAsync(Memory, cancellationToken);
            var readings = prometheus.InstantAsync(ReadingRate, cancellationToken);
            var requestSeries = prometheus.RangeAsync(RequestRate, ChartWindow, ChartStep, now, cancellationToken);
            var errorSeries = prometheus.RangeAsync(ErrorRate, ChartWindow, ChartStep, now, cancellationToken);
            var latencySeries = prometheus.RangeAsync(Latency, ChartWindow, ChartStep, now, cancellationToken);

            await Task.WhenAll(requests, errors, latency, ready, desired, restarts, memory, readings, requestSeries, errorSeries, latencySeries);

            var podsReady = (int?)ready.Result;
            var podsDesired = (int?)desired.Result;

            return new SiteHealthReport(
                Overall(dependencies, errors.Result, podsReady, podsDesired),
                MetricsAvailable: true,
                requests.Result,
                errors.Result,
                latency.Result,
                podsReady,
                podsDesired,
                restarts.Result,
                memory.Result,
                readings.Result,
                requestSeries.Result,
                errorSeries.Result,
                latencySeries.Result,
                dependencies);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Couldn't read metrics from Prometheus");
            return new SiteHealthReport(
                Overall(dependencies, null, null, null),
                MetricsAvailable: false,
                null, null, null, null, null, null, null, null, [], [], [],
                dependencies);
        }
    }

    public static string Overall(IReadOnlyList<DependencyHealth> dependencies, double? errorPercent, int? podsReady, int? podsDesired)
    {
        var degraded = dependencies.Any(dependency => dependency.State == "down")
            || errorPercent >= DegradedErrorPercent
            || (podsReady is not null && podsDesired is not null && podsReady < podsDesired);

        return degraded ? "Degraded" : "Healthy";
    }

    private async Task<IReadOnlyList<DependencyHealth>> DependenciesAsync(CancellationToken cancellationToken) =>
    [
        await ElasticsearchAsync(cancellationToken),
        new("OpenAI", backends.OpenAI is null ? "off" : "ok", backends.OpenAI is null ? "No key, so photo reports are off" : "Key configured"),
        new("Dash0", backends.TelemetryExportEnabled ? "ok" : "off", backends.TelemetryExportEnabled ? "Exporting traces, logs and metrics" : "Not configured"),
        new("Sensors", sensors.Enabled ? "ok" : "off", sensors.Enabled ? "Setup key configured" : "No setup key, so sensors are off"),
    ];

    private async Task<DependencyHealth> ElasticsearchAsync(CancellationToken cancellationToken)
    {
        if (backends.Elasticsearch is not { } client)
        {
            return new DependencyHealth("Elasticsearch", "off", "Not configured, using sample data");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await client.PingAsync(timeout.Token);
            return response.IsValidResponse
                ? new DependencyHealth("Elasticsearch", "ok", $"Responded in {stopwatch.ElapsedMilliseconds} ms")
                : new DependencyHealth("Elasticsearch", "down", "Rejected the request");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or Elastic.Transport.TransportException)
        {
            return new DependencyHealth("Elasticsearch", "down", "Not responding");
        }
    }
}
