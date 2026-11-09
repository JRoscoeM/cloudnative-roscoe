using App;
using Elastic.Clients.Elasticsearch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

var info = new AppInfo(
    Team: builder.Configuration["TEAM_NAME"] ?? "your-team",
    Version: builder.Configuration["APP_VERSION"] ?? "dev");

builder.Services.AddSingleton(info);
var backends = Backends.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(backends);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<BoardService>();
builder.Services.AddSingleton(new SensorAuth(builder.Configuration["SENSOR_KEY"]));
builder.Services.AddSingleton<IReadingStore>(services => backends.Elasticsearch is { } elastic
    ? new ElasticReadingStore(elastic)
    : new LocalReadingStore(services.GetRequiredService<TimeProvider>()));
if (backends.OpenAI is { } openAi)
{
    builder.Services.AddSingleton<ISnapshotAnalyzer>(
        new OpenAiSnapshotAnalyzer(openAi, builder.Configuration["OPENAI_MODEL"] ?? "gpt-5-mini"));
}

builder.Services.AddSingleton<IVenueStore>(_ => backends.Elasticsearch is { } venueClient
    ? new ElasticVenueStore(venueClient)
    : new LocalVenueStore());
builder.Services.AddSingleton<VenueService>();
builder.Services.AddSingleton(new OrganizerAuth(
    builder.Configuration["ORGANIZER_KEY"] is { Length: > 0 } organizerKey ? organizerKey : builder.Configuration["SENSOR_KEY"]));
builder.Services.AddSingleton<SiteHealthService>();
builder.Services.AddHttpClient<PrometheusClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PROMETHEUS_URL"] ?? "http://prometheus-server.monitoring.svc.cluster.local");
    client.Timeout = TimeSpan.FromSeconds(3);
});

builder.Services.AddSingleton<SearchBudget>();
builder.Services.AddSingleton<TalkFinder>();
if (backends.Elasticsearch is { } talksClient)
{
    var talkSearch = new ElasticTalkSearch(talksClient);
    builder.Services.AddSingleton(talkSearch);
    builder.Services.AddSingleton<ITalkSearch>(talkSearch);
    builder.Services.AddHostedService<TalkIndexer>();
}
else
{
    builder.Services.AddSingleton<ITalkSearch, LocalTalkSearch>();
}

builder.Services.AddOccupancyRateLimits();
// Antiforgery keys live in each pod, so a form rendered by one replica would be
// rejected by the other. The app has no login or cookies for CSRF to abuse; add
// shared Data Protection keys before removing this or adding authentication.
builder.Services.AddRazorPages(options =>
    options.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute()));
builder.AddAppTelemetry(info);

var app = builder.Build();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".mjs"] = "text/javascript";
contentTypes.Mappings[".tflite"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = context =>
    {
        // The detector is about 17 MB and pinned to a version, so phones keep it.
        if (context.Context.Request.Path.StartsWithSegments("/vendor"))
        {
            context.Context.Response.Headers.CacheControl = "public, max-age=86400";
        }
    },
});
app.UseRateLimiter();
app.MapRazorPages();
app.MapOccupancyApi();
app.MapOrganizerApi();

// Kubernetes probes. Deliberately independent of Elasticsearch and OpenAI: an
// outage at a provider should show as errors, not as pods being restarted.
app.MapGet("/healthz", () => Results.Text("ok\n"));
app.MapGet("/readyz", () => Results.Text("ok\n"));

// Scraped by the cluster's Prometheus, queried through Radar.
app.MapPrometheusScrapingEndpoint();

// The deploy checks read `version` here to prove a build is live.
app.MapGet("/api/sessions", (AppInfo appInfo) =>
    Results.Json(new { version = appInfo.Version, sessions = Schedule.Sessions }));

// Which providers are wired up. Never returns a credential.
app.MapGet("/api/status", async (AppInfo appInfo, Backends backends, CancellationToken cancellationToken) =>
    Results.Json(new
    {
        version = appInfo.Version,
        team = appInfo.Team,
        elasticsearch = new
        {
            configured = backends.Elasticsearch is not null,
            reachable = await IsReachableAsync(backends.Elasticsearch, cancellationToken),
        },
        openai = new { configured = backends.OpenAI is not null },
        dash0 = new { configured = backends.TelemetryExportEnabled },
    }));

app.Run();

static async Task<bool> IsReachableAsync(ElasticsearchClient? client, CancellationToken cancellationToken)
{
    if (client is null)
    {
        return false;
    }

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        var response = await client.PingAsync(timeout.Token);
        return response.IsValidResponse;
    }
    catch (Exception e) when (e is HttpRequestException or OperationCanceledException or Elastic.Transport.TransportException)
    {
        return false;
    }
}
