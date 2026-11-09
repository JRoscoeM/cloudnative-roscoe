using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.AspNetCore.Mvc.Testing;

namespace App.Tests;

public class OrganizerApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Key = "test-organizer-key";

    private WebApplicationFactory<Program> Host() => factory.WithWebHostBuilder(host =>
    {
        host.UseSetting("SENSOR_KEY", "test-sensor-key");
        host.UseSetting("ORGANIZER_KEY", Key);
        host.UseSetting("ELASTICSEARCH_URL", "");
        host.UseSetting("ELASTICSEARCH_API_KEY", "");
        host.UseSetting("OPENAI_API_KEY", "");
        host.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "");
        host.UseSetting("PROMETHEUS_URL", "http://127.0.0.1:9");
    });

    private async Task<HttpClient> SignedInAsync()
    {
        var client = Host().CreateClient();
        var response = await client.PostAsJsonAsync("/api/organizer/session", new { key = Key });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.RootElement.GetProperty("token").GetString());
        return client;
    }

    [Fact]
    public async Task A_wrong_key_cannot_sign_in()
    {
        var response = await Host().CreateClient().PostAsJsonAsync("/api/organizer/session", new { key = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_sensor_key_does_not_open_the_dashboard_when_an_organizer_key_exists()
    {
        var response = await Host().CreateClient().PostAsJsonAsync("/api/organizer/session", new { key = "test-sensor-key" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/organizer/overview")]
    [InlineData("POST", "/api/organizer/rooms")]
    [InlineData("PUT", "/api/organizer/rooms/room-101")]
    [InlineData("DELETE", "/api/organizer/rooms/room-101")]
    [InlineData("POST", "/api/organizer/queues")]
    [InlineData("POST", "/api/organizer/sensor-links/room-101")]
    public async Task Every_organizer_route_needs_a_token(string method, string path)
    {
        var request = new HttpRequestMessage(new System.Net.Http.HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { name = "x", capacity = 1, secondsPerPerson = 1 });
        }

        var response = await Host().CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_added_room_appears_on_the_public_map()
    {
        var client = await SignedInAsync();

        (await client.PostAsJsonAsync("/api/organizer/rooms", new { name = "Expo Hall", capacity = 40 })).EnsureSuccessStatusCode();
        var html = await client.GetStringAsync("/?handler=Live");

        Assert.Contains("Expo Hall", html);
    }

    [Fact]
    public async Task A_new_capacity_changes_the_board()
    {
        var client = await SignedInAsync();

        (await client.PutAsJsonAsync("/api/organizer/rooms/room-101", new { name = "Renamed", capacity = 8 })).EnsureSuccessStatusCode();
        var html = await client.GetStringAsync("/?handler=Live");

        Assert.Contains("Renamed", html);
        Assert.Contains("of 8", html);
    }

    [Fact]
    public async Task A_queue_can_be_added_and_deleted()
    {
        var client = await SignedInAsync();

        (await client.PostAsJsonAsync("/api/organizer/queues", new { name = "Swag table", secondsPerPerson = 40 })).EnsureSuccessStatusCode();
        (await client.DeleteAsync("/api/organizer/queues/swag-table")).EnsureSuccessStatusCode();

        Assert.DoesNotContain("Swag table", await client.GetStringAsync("/api/organizer/overview"));
    }

    [Fact]
    public async Task The_last_room_cannot_be_deleted()
    {
        var client = await SignedInAsync();

        await client.DeleteAsync("/api/organizer/rooms/main-hall");
        await client.DeleteAsync("/api/organizer/rooms/room-101");
        var response = await client.DeleteAsync("/api/organizer/rooms/room-102");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("", 10)]
    [InlineData("   ", 10)]
    [InlineData("A name that is far too long to fit anywhere", 10)]
    [InlineData("Fine", 0)]
    [InlineData("Fine", 100001)]
    public async Task Bad_room_details_are_rejected(string name, int capacity)
    {
        var client = await SignedInAsync();

        var response = await client.PostAsJsonAsync("/api/organizer/rooms", new { name, capacity });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_sensor_link_carries_a_working_token_for_that_space()
    {
        var client = await SignedInAsync();

        var response = await client.PostAsync("/api/organizer/sensor-links/room-102", null);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var path = json.RootElement.GetProperty("path").GetString()!;
        var token = path[(path.IndexOf("token=", StringComparison.Ordinal) + 6)..];

        var report = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, "/api/readings")
        {
            Content = JsonContent.Create(new { target = "room-102", people = 2, confidence = 0.8 }),
        };
        report.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.StartsWith("/sensor#target=room-102&token=", path);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(report)).StatusCode);
    }

    [Fact]
    public async Task The_dashboard_still_works_without_prometheus()
    {
        var client = await SignedInAsync();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/organizer/overview"));
        var health = json.RootElement.GetProperty("health");

        Assert.False(health.GetProperty("metricsAvailable").GetBoolean());
        Assert.Equal("Healthy", health.GetProperty("overall").GetString());
        Assert.True(json.RootElement.GetProperty("rooms").GetArrayLength() > 0);
    }

    [Fact]
    public async Task A_room_added_by_an_organizer_can_have_a_sensor()
    {
        var client = await SignedInAsync();
        (await client.PostAsJsonAsync("/api/organizer/rooms", new { name = "Expo Hall", capacity = 40 })).EnsureSuccessStatusCode();

        var sensorPage = await client.GetStringAsync("/sensor");
        var snapshotPage = await client.GetStringAsync("/snapshot");

        Assert.Contains("expo-hall", sensorPage);
        Assert.Contains("expo-hall", snapshotPage);
    }

    [Fact]
    public async Task The_organizer_page_is_served()
    {
        (await Host().CreateClient().GetAsync("/organizer")).EnsureSuccessStatusCode();
    }
}

public class FloorPlanTests
{
    private static VenueSnapshot Venue(int rooms, int queues) => new(
        Enumerable.Range(1, rooms).Select(i => new RoomDefinition($"r{i}", $"Room {i}", 10)).ToList(),
        Enumerable.Range(1, queues).Select(i => new QueueDefinition($"q{i}", $"Queue {i}", $"Q{i}", 30)).ToList());

    private static bool Overlap(MapArea a, MapArea b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 1)]
    [InlineData(9, 7)]
    public void Nothing_overlaps_or_leaves_the_map(int rooms, int queues)
    {
        var layout = FloorPlan.Compute(Venue(rooms, queues));
        var areas = layout.Areas.Values.ToList();

        Assert.Equal(rooms + queues, areas.Count);
        Assert.All(areas, area => Assert.True(area.X >= 0 && area.X + area.Width <= 360 && area.Y + area.Height <= layout.Height));
        for (var i = 0; i < areas.Count; i++)
        {
            for (var j = i + 1; j < areas.Count; j++)
            {
                Assert.False(Overlap(areas[i], areas[j]));
            }
        }
    }

    [Fact]
    public void The_first_room_spans_the_full_width()
    {
        Assert.Equal(344, FloorPlan.Compute(Venue(3, 0)).Areas["r1"].Width);
    }

    [Fact]
    public void A_long_name_is_shortened_on_a_half_width_tile_only()
    {
        const string name = "An Extremely Long Room Name";
        var half = new RoomStatus("a", name, null, 10, 1, null, new MapArea(0, 0, 168, 96));
        var wide = half with { Area = new MapArea(0, 0, 344, 96) };

        Assert.True(half.MapName.Length <= 18);
        Assert.Equal(name, wide.MapName);
    }
}

public class VenueIdsTests
{
    private static readonly VenueSnapshot Existing = new([new RoomDefinition("expo-hall", "Expo", 1)], [new QueueDefinition("coffee", "Coffee", "Coffee", 1)]);

    [Theory]
    [InlineData("Grand Ballroom", "grand-ballroom")]
    [InlineData("  Room #1!  ", "room-1")]
    [InlineData("!!!", "space")]
    [InlineData("Expo Hall", "expo-hall-2")]
    [InlineData("Coffee", "coffee-2")]
    public void Ids_are_url_safe_and_unique(string name, string expected)
    {
        Assert.Equal(expected, VenueIds.New(name, Existing));
    }
}

public class SiteHealthRulesTests
{
    private static readonly DependencyHealth Ok = new("Elasticsearch", "ok", "");

    [Theory]
    [InlineData(0.0, 2, 2, "Healthy")]
    [InlineData(4.9, 2, 2, "Healthy")]
    [InlineData(5.0, 2, 2, "Degraded")]
    [InlineData(0.0, 1, 2, "Degraded")]
    public void Overall_status_follows_errors_and_pods(double errors, int ready, int desired, string expected)
    {
        Assert.Equal(expected, SiteHealthService.Overall([Ok], errors, ready, desired));
    }

    [Fact]
    public void A_down_dependency_degrades_the_site()
    {
        Assert.Equal("Degraded", SiteHealthService.Overall([new("Elasticsearch", "down", "")], null, null, null));
    }

    [Fact]
    public void A_service_that_is_switched_off_is_not_a_failure()
    {
        Assert.Equal("Healthy", SiteHealthService.Overall([new("OpenAI", "off", "")], null, null, null));
    }
}

public class PrometheusClientTests
{
    private sealed class Canned(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static PrometheusClient Client(string json) =>
        new(new HttpClient(new Canned(json)) { BaseAddress = new Uri("http://prometheus.test") });

    [Fact]
    public async Task Reads_an_instant_value()
    {
        var value = await Client("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1,"2.5"]}]}}""")
            .InstantAsync("up", CancellationToken.None);

        Assert.Equal(2.5, value);
    }

    [Fact]
    public async Task No_series_means_no_value()
    {
        Assert.Null(await Client("""{"status":"success","data":{"resultType":"vector","result":[]}}""").InstantAsync("up", CancellationToken.None));
    }

    [Fact]
    public async Task NaN_means_no_value()
    {
        Assert.Null(await Client("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1,"NaN"]}]}}""").InstantAsync("up", CancellationToken.None));
    }

    [Fact]
    public async Task A_range_drops_gaps()
    {
        var points = await Client("""{"status":"success","data":{"resultType":"matrix","result":[{"metric":{},"values":[[1,"2"],[2,"NaN"],[3,"4"]]}]}}""")
            .RangeAsync("up", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal([new MetricPoint(1, 2), new MetricPoint(3, 4)], points);
    }
}

public class ElasticVenueStoreTests
{
    private static readonly Dictionary<string, IEnumerable<string>> ProductHeader = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x-elastic-product"] = ["Elasticsearch"],
    };

    private readonly List<(string Method, string Path, string Body)> _requests = [];

    private ElasticVenueStore Store(string responseJson)
    {
        var settings = new ElasticsearchClientSettings(
                new SingleNodePool(new Uri("http://elastic.test:9200")),
                new InMemoryRequestInvoker(Encoding.UTF8.GetBytes(responseJson), 200, headers: ProductHeader))
            .DisableDirectStreaming()
            .ResponseHeadersToParse(new HeadersList("x-elastic-product"))
            .OnRequestCompleted(call => _requests.Add((
                call.HttpMethod.ToString(),
                call.Uri!.AbsolutePath,
                call.RequestBodyInBytes is { } body ? Encoding.UTF8.GetString(body) : "")));

        return new ElasticVenueStore(new ElasticsearchClient(settings));
    }

    private const string Saved = """{"_index":"headroom-venue","_id":"expo","result":"updated","_version":2,"_seq_no":1,"_primary_term":1,"_shards":{"total":1,"successful":1,"failed":0}}""";

    [Fact]
    public async Task A_room_is_written_under_its_own_id()
    {
        await Store(Saved).SaveRoomAsync(new RoomDefinition("expo", "Expo", 40), CancellationToken.None);

        var write = _requests.Last();
        Assert.Equal("/headroom-venue/_doc/expo", write.Path);
        Assert.Contains("\"capacity\":40", write.Body);
    }

    [Fact]
    public async Task A_delete_targets_the_venue_index()
    {
        await Store(Saved).DeleteAsync("expo", CancellationToken.None);

        var delete = _requests.Single();
        Assert.Equal("DELETE", delete.Method);
        Assert.Equal("/headroom-venue/_doc/expo", delete.Path);
    }

    [Fact]
    public async Task Stored_rooms_and_queues_are_read_back()
    {
        var venue = await Store("""
            {
              "took":1,"timed_out":false,"_shards":{"total":1,"successful":1,"skipped":0,"failed":0},
              "hits":{"total":{"value":2,"relation":"eq"},"hits":[
                {"_index":"headroom-venue","_id":"expo","_score":1,"_source":{"kind":"room","id":"expo","name":"Expo","shortName":"","capacity":40,"secondsPerPerson":0}},
                {"_index":"headroom-venue","_id":"tea","_score":1,"_source":{"kind":"queue","id":"tea","name":"Tea","shortName":"Tea","capacity":0,"secondsPerPerson":15}}
              ]}
            }
            """).LoadAsync(CancellationToken.None);

        Assert.Equal(new RoomDefinition("expo", "Expo", 40), Assert.Single(venue.Rooms));
        Assert.Equal(new QueueDefinition("tea", "Tea", "Tea", 15), Assert.Single(venue.Queues));
    }
}
