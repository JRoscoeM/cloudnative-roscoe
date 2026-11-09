using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace App.Tests;

public class OccupancyApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string SetupKey = "test-setup-key";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private sealed class FakeAnalyzer(SnapshotEstimate estimate) : ISnapshotAnalyzer
    {
        public Task<SnapshotEstimate> AnalyzeAsync(byte[] jpeg, RoomDefinition room, CancellationToken cancellationToken) =>
            Task.FromResult(estimate);
    }

    private sealed class FailingStore : IReadingStore
    {
        public bool IsSample => false;

        public Task AddAsync(Reading reading, CancellationToken cancellationToken) =>
            throw new ReadingStoreException("down");

        public Task<IReadOnlyList<Reading>> SinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
            throw new ReadingStoreException("down");
    }

    private WebApplicationFactory<Program> Host(string? setupKey = SetupKey, Action<IServiceCollection>? services = null) =>
        factory.WithWebHostBuilder(host =>
        {
            host.UseSetting("SENSOR_KEY", setupKey ?? "");
            host.UseSetting("ELASTICSEARCH_URL", "");
            host.UseSetting("ELASTICSEARCH_API_KEY", "");
            host.UseSetting("OPENAI_API_KEY", "");
            host.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "");
            if (services is not null)
            {
                host.ConfigureTestServices(services);
            }
        });

    private static async Task<string> TokenAsync(HttpClient client, string target)
    {
        var response = await client.PostAsJsonAsync("/api/sensor/token", new { target, setupKey = SetupKey });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> ReportAsync(HttpClient client, string target, int people, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/readings")
        {
            Content = JsonContent.Create(new { target, people, confidence = 0.8 }),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client.SendAsync(request);
    }

    private static MultipartFormDataContent Photo(string room, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new MultipartFormDataContent { { new StringContent(room), "room" }, { content, "photo", "room.jpg" } };
    }

    [Fact]
    public async Task A_sensor_reading_reaches_the_board()
    {
        var client = Host().CreateClient();
        var token = await TokenAsync(client, "room-101");

        var response = await ReportAsync(client, "room-101", 42, token);
        var html = await client.GetStringAsync("/?handler=Live");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("42 of 120", html);
    }

    [Fact]
    public async Task A_queue_reading_becomes_a_wait_time()
    {
        var client = Host().CreateClient();
        var token = await TokenAsync(client, "coffee");

        await ReportAsync(client, "coffee", 9, token);
        var html = await client.GetStringAsync("/?handler=Live");

        Assert.Contains("9 in line", html);
        Assert.Contains("3 min", html);
    }

    [Fact]
    public async Task A_wrong_setup_key_gets_no_token()
    {
        var response = await Host().CreateClient()
            .PostAsJsonAsync("/api/sensor/token", new { target = "room-101", setupKey = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_reading_without_a_token_is_rejected()
    {
        var response = await ReportAsync(Host().CreateClient(), "room-101", 42, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task One_rooms_token_cannot_report_for_another()
    {
        var client = Host().CreateClient();
        var token = await TokenAsync(client, "room-101");

        var response = await ReportAsync(client, "room-102", 42, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(241)]
    public async Task An_impossible_count_is_rejected(int people)
    {
        var client = Host().CreateClient();
        var token = await TokenAsync(client, "room-101");

        var response = await ReportAsync(client, "room-101", people, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sensors_are_refused_until_a_setup_key_is_configured()
    {
        var response = await Host(setupKey: null).CreateClient()
            .PostAsJsonAsync("/api/sensor/token", new { target = "room-101", setupKey = "" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Snapshots_are_refused_until_openai_is_configured()
    {
        var response = await Host().CreateClient().PostAsync("/api/snapshots", Photo("room-101", Jpeg));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private WebApplicationFactory<Program> HostWithAnalyzer(SnapshotEstimate estimate) =>
        Host(services: s => s.AddSingleton<ISnapshotAnalyzer>(new FakeAnalyzer(estimate)));

    [Fact]
    public async Task A_snapshot_updates_the_board_from_fullness()
    {
        var client = HostWithAnalyzer(new SnapshotEstimate(30, 50, 40, 0.9)).CreateClient();

        var response = await client.PostAsync("/api/snapshots", Photo("room-101", Jpeg));
        var html = await client.GetStringAsync("/?handler=Live");

        response.EnsureSuccessStatusCode();
        Assert.Contains("60 of 120", html);
        Assert.Contains("Photo, just now", html);
    }

    [Fact]
    public async Task A_low_confidence_snapshot_is_not_counted()
    {
        var client = HostWithAnalyzer(new SnapshotEstimate(30, 50, 40, 0.1)).CreateClient();

        var response = await client.PostAsync("/api/snapshots", Photo("room-101", Jpeg));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(json.RootElement.GetProperty("counted").GetBoolean());
    }

    [Fact]
    public async Task A_snapshot_for_an_unknown_room_is_rejected()
    {
        var response = await HostWithAnalyzer(new SnapshotEstimate(1, 1, 1, 1)).CreateClient()
            .PostAsync("/api/snapshots", Photo("nowhere", Jpeg));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_jpegs_are_accepted()
    {
        var response = await HostWithAnalyzer(new SnapshotEstimate(1, 1, 1, 1)).CreateClient()
            .PostAsync("/api/snapshots", Photo("room-101", [1, 2, 3, 4]));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Snapshots_are_rate_limited()
    {
        var client = HostWithAnalyzer(new SnapshotEstimate(30, 50, 40, 0.9)).CreateClient();

        HttpStatusCode last = default;
        for (var attempt = 0; attempt < 11; attempt++)
        {
            last = (await client.PostAsync("/api/snapshots", Photo("room-101", Jpeg))).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task The_home_page_survives_a_database_outage()
    {
        var client = Host(services: s => s.AddSingleton<IReadingStore>(new FailingStore())).CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("Live counts are unavailable", html);
    }

    [Fact]
    public async Task A_failed_save_is_reported_to_the_sensor()
    {
        var client = Host(services: s => s.AddSingleton<IReadingStore>(new FailingStore())).CreateClient();
        var token = await TokenAsync(client, "room-101");

        var response = await ReportAsync(client, "room-101", 42, token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("/sensor")]
    [InlineData("/snapshot")]
    [InlineData("/sensor.js")]
    [InlineData("/snapshot.js")]
    [InlineData("/vendor/mediapipe/vision_bundle.mjs")]
    [InlineData("/vendor/mediapipe/wasm/vision_wasm_internal.wasm")]
    [InlineData("/vendor/mediapipe/efficientdet_lite2.tflite")]
    public async Task Pages_and_detector_files_are_served(string path)
    {
        var response = await Host().CreateClient().GetAsync(path);

        response.EnsureSuccessStatusCode();
    }
}
