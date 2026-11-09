using System.Text;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;

namespace App.Tests;

/// <summary>
/// Runs the real client against a fake connection, to check the requests it
/// actually sends and that the documents read back.
/// </summary>
public class ElasticReadingStoreTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, IEnumerable<string>> ProductHeader = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x-elastic-product"] = ["Elasticsearch"],
    };

    private readonly List<(string Method, string Path, string Body)> _requests = [];

    private ElasticReadingStore Store(string responseJson, int status = 200)
    {
        var settings = new ElasticsearchClientSettings(
                new SingleNodePool(new Uri("http://elastic.test:9200")),
                new InMemoryRequestInvoker(Encoding.UTF8.GetBytes(responseJson), status, headers: ProductHeader))
            .DisableDirectStreaming()
            .ResponseHeadersToParse(new HeadersList("x-elastic-product"))
            .OnRequestCompleted(call => _requests.Add((
                call.HttpMethod.ToString(),
                call.Uri!.AbsolutePath,
                call.RequestBodyInBytes is { } body ? Encoding.UTF8.GetString(body) : "")));

        return new ElasticReadingStore(new ElasticsearchClient(settings));
    }

    [Fact]
    public async Task A_reading_is_written_to_the_readings_index()
    {
        var store = Store("""{"_index":"headroom-readings","_id":"1","result":"created","_version":1,"_seq_no":0,"_primary_term":1,"_shards":{"total":1,"successful":1,"failed":0}}""", 201);

        await store.AddAsync(new Reading("coffee", ReadingSource.Sensor, 3, 0.8, CapturedAt), CancellationToken.None);

        var write = _requests.Last();
        Assert.Equal("/headroom-readings/_doc", write.Path);
    }

    [Fact]
    public async Task Written_fields_match_the_index_mapping()
    {
        var store = Store("""{"_index":"headroom-readings","_id":"1","result":"created","_version":1,"_seq_no":0,"_primary_term":1,"_shards":{"total":1,"successful":1,"failed":0}}""", 201);

        await store.AddAsync(new Reading("coffee", ReadingSource.Sensor, 3, 0.8, CapturedAt), CancellationToken.None);

        var body = _requests.Last().Body;
        foreach (var field in new[] { "\"target\":\"coffee\"", "\"source\":\"Sensor\"", "\"people\":3", "\"capturedAt\":" })
        {
            Assert.Contains(field, body);
        }
    }

    [Fact]
    public async Task Readings_are_read_back_from_the_readings_index()
    {
        var store = Store("""
            {
              "took": 1, "timed_out": false,
              "_shards": { "total": 1, "successful": 1, "skipped": 0, "failed": 0 },
              "hits": {
                "total": { "value": 1, "relation": "eq" },
                "hits": [{
                  "_index": "headroom-readings", "_id": "1", "_score": null,
                  "_source": { "target": "coffee", "source": "Snapshot", "people": 3, "confidence": 0.8, "capturedAt": "2026-11-09T20:00:00+00:00" }
                }]
              }
            }
            """);

        var readings = await store.SinceAsync(CapturedAt.AddMinutes(-15), CancellationToken.None);

        var search = _requests.Last();
        Assert.Equal("/headroom-readings/_search", search.Path);
        Assert.Contains("capturedAt", search.Body);
        Assert.Equal(new Reading("coffee", ReadingSource.Snapshot, 3, 0.8, CapturedAt), Assert.Single(readings));
    }

    [Fact]
    public async Task A_rejected_write_is_reported_as_a_store_failure()
    {
        var store = Store("""{"error":{"type":"security_exception","reason":"denied"},"status":403}""", 403);

        await Assert.ThrowsAsync<ReadingStoreException>(() =>
            store.AddAsync(new Reading("coffee", ReadingSource.Sensor, 3, 0.8, CapturedAt), CancellationToken.None));
    }
}
