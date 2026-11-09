using App;
using Elastic.Clients.Elasticsearch;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var info = new AppInfo(
    Team: builder.Configuration["TEAM_NAME"] ?? "your-team",
    Version: builder.Configuration["APP_VERSION"] ?? "dev");

builder.Services.AddSingleton(info);
builder.Services.AddSingleton(Backends.FromConfiguration(builder.Configuration));
// Antiforgery keys live in each pod, so a form rendered by one replica would be
// rejected by the other. The app has no login or cookies for CSRF to abuse; add
// shared Data Protection keys before removing this or adding authentication.
builder.Services.AddRazorPages(options =>
    options.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute()));
builder.AddAppTelemetry(info);

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorPages();

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
