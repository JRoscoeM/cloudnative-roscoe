using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace App;

public interface IReadingStore
{
    /// <summary>True when the numbers are examples, not real readings.</summary>
    bool IsSample { get; }

    Task AddAsync(Reading reading, CancellationToken cancellationToken);

    Task<IReadOnlyList<Reading>> SinceAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

public sealed class ReadingStoreException(string message) : Exception(message);

/// <summary>Stores readings in Elasticsearch, shared by every replica.</summary>
public sealed class ElasticReadingStore(ElasticsearchClient client) : IReadingStore
{
    public const string IndexName = "headroom-readings";
    private const int MaxReadings = 2000;

    private volatile bool _indexReady;

    public bool IsSample => false;

    public async Task AddAsync(Reading reading, CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken);

        // Set explicitly: a string passed positionally here is taken as the document id.
        var response = await client.IndexAsync(
            ReadingDocument.From(reading),
            request => request.Index(IndexName),
            cancellationToken);
        if (!response.IsValidResponse)
        {
            throw new ReadingStoreException($"Couldn't save a reading: {response.ElasticsearchServerError?.Error?.Type}");
        }
    }

    public async Task<IReadOnlyList<Reading>> SinceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken);

        var request = new SearchRequest<ReadingDocument>(IndexName)
        {
            Size = MaxReadings,
            Query = new DateRangeQuery("capturedAt") { Gte = since.UtcDateTime },
            Sort = [new SortOptions { Field = new FieldSort { Field = "capturedAt", Order = SortOrder.Desc } }],
        };

        var response = await client.SearchAsync<ReadingDocument>(request, cancellationToken);
        if (!response.IsValidResponse)
        {
            throw new ReadingStoreException($"Couldn't read readings: {response.ElasticsearchServerError?.Error?.Type}");
        }

        return response.Documents.Select(document => document.ToReading()).ToList();
    }

    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_indexReady)
        {
            return;
        }

        var request = new Elastic.Clients.Elasticsearch.IndexManagement.CreateIndexRequest(IndexName)
        {
            Mappings = new TypeMapping
            {
                Properties = new Properties
                {
                    { "target", new KeywordProperty() },
                    { "source", new KeywordProperty() },
                    { "people", new IntegerNumberProperty() },
                    { "confidence", new FloatNumberProperty() },
                    { "capturedAt", new DateProperty() },
                },
            },
        };

        var response = await client.Indices.CreateAsync(request, cancellationToken);

        // Both replicas race to create it on first use, so "already exists" is success.
        if (!response.IsValidResponse && response.ElasticsearchServerError?.Error?.Type != "resource_already_exists_exception")
        {
            throw new ReadingStoreException($"Couldn't create the index: {response.ElasticsearchServerError?.Error?.Type}");
        }

        _indexReady = true;
    }

    // Internal, not private: the client's JSON serializer has to reach it.
    internal sealed class ReadingDocument
    {
        public string Target { get; set; } = "";
        public string Source { get; set; } = "";
        public int People { get; set; }
        public double Confidence { get; set; }
        public DateTimeOffset CapturedAt { get; set; }

        public static ReadingDocument From(Reading reading) => new()
        {
            Target = reading.Target,
            Source = reading.Source.ToString(),
            People = reading.People,
            Confidence = reading.Confidence,
            CapturedAt = reading.CapturedAt,
        };

        public Reading ToReading() =>
            new(Target, Enum.Parse<ReadingSource>(Source), People, Confidence, CapturedAt);
    }
}

/// <summary>
/// For running without Elasticsearch, on a laptop or in tests. Shows example
/// readings for any space nothing has reported on. Never used once a database is
/// configured, because this memory isn't shared between replicas.
/// </summary>
public sealed class LocalReadingStore(TimeProvider time) : IReadingStore
{
    private static readonly (string Target, int From, int To)[] Examples =
    [
        ("main-hall", 560, 612),
        ("room-101", 95, 116),
        ("room-102", 31, 31),
        ("room-201", 60, 60),
        ("room-202", 52, 66),
        ("coffee", 14, 14),
        ("lunch", 38, 38),
        ("badges", 4, 4),
    ];

    private readonly Lock _gate = new();
    private readonly List<Reading> _readings = [];

    public bool IsSample => true;

    public Task AddAsync(Reading reading, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _readings.Add(reading);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Reading>> SinceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        List<Reading> reported;
        lock (_gate)
        {
            reported = _readings.Where(reading => reading.CapturedAt >= since).ToList();
        }

        var reportedTargets = reported.Select(reading => reading.Target).ToHashSet();
        var examples = Examples.Where(example => !reportedTargets.Contains(example.Target)).SelectMany(ExampleSeries);

        return Task.FromResult<IReadOnlyList<Reading>>([.. reported, .. examples]);
    }

    private IEnumerable<Reading> ExampleSeries((string Target, int From, int To) example)
    {
        var now = time.GetUtcNow();
        for (var step = 0; step <= 4; step++)
        {
            var people = example.From + ((example.To - example.From) * step / 4);
            yield return new Reading(example.Target, ReadingSource.Sensor, people, 0.8, now.AddMinutes(-8 + (2 * step)));
        }
    }
}
