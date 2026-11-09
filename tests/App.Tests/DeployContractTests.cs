using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace App.Tests;

/// <summary>
/// What the manifests in deploy/ and the ship checks depend on. A failure here
/// means a deploy would look broken even if the feature work is fine.
/// </summary>
public class DeployContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string BuiltVersion = "abc1234";
    private readonly HttpClient _client;

    public DeployContractTests(WebApplicationFactory<Program> factory)
    {
        _client = factory
            .WithWebHostBuilder(host =>
            {
                host.UseSetting("APP_VERSION", BuiltVersion);
                host.UseSetting("TEAM_NAME", "test-team");
                host.UseSetting("ELASTICSEARCH_URL", "");
                host.UseSetting("ELASTICSEARCH_API_KEY", "");
                host.UseSetting("OPENAI_API_KEY", "");
                host.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "");
            })
            .CreateClient();
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    public async Task Probes_answer_ok(string path)
    {
        var response = await _client.GetAsync(path);

        response.EnsureSuccessStatusCode();
        Assert.Equal("ok\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Home_page_shows_the_built_version()
    {
        var html = await _client.GetStringAsync("/");

        Assert.Contains($"version {BuiltVersion}", html);
    }

    [Fact]
    public async Task Sessions_api_reports_the_built_version()
    {
        using var json = JsonDocument.Parse(await _client.GetStringAsync("/api/sessions"));

        Assert.Equal(BuiltVersion, json.RootElement.GetProperty("version").GetString());
        Assert.True(json.RootElement.GetProperty("sessions").GetArrayLength() > 0);
    }

    [Fact]
    public async Task App_runs_without_any_provider_credentials()
    {
        using var json = JsonDocument.Parse(await _client.GetStringAsync("/api/status"));

        Assert.False(json.RootElement.GetProperty("elasticsearch").GetProperty("configured").GetBoolean());
        Assert.False(json.RootElement.GetProperty("openai").GetProperty("configured").GetBoolean());
        Assert.False(json.RootElement.GetProperty("dash0").GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Metrics_are_exposed_for_prometheus()
    {
        var response = await _client.GetAsync("/metrics");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Search_returns_a_fragment_for_htmx()
    {
        var html = await _client.GetStringAsync("/?handler=Results&q=panel");

        Assert.Contains("Panel", html);
        Assert.DoesNotContain("Kickoff", html);
        Assert.DoesNotContain("<html", html);
    }
}
