using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Caching.Memory;

namespace App;

public sealed class TalkSearchException(string message) : Exception(message);

public interface ITalkSearch
{
    /// <summary>Session ids, most relevant first.</summary>
    Task<IReadOnlyList<int>> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>Other sessions on a similar topic, closest first.</summary>
    Task<IReadOnlyList<int>> SimilarAsync(Session session, CancellationToken cancellationToken);
}

/// <summary>
/// Talks in Elasticsearch. Hand-written JSON, because semantic_text and retrievers
/// are simpler to express directly than through the typed client.
/// </summary>
public sealed class ElasticTalkSearch(ElasticsearchClient client) : ITalkSearch
{
    public const string IndexName = "livevision-talks";
    private const int SearchResultCount = 8;

    // Most similar talks won't be on right now, so fetch enough to find one that is.
    private const int SimilarResultCount = 40;
    private const int BulkBatchSize = 50;

    private const string Mapping = """
        {
          "mappings": {
            "properties": {
              "sessionId": { "type": "integer" },
              "title": { "type": "text" },
              "semantic_content": { "type": "semantic_text" }
            }
          }
        }
        """;

    /// <summary>Creates the index if needed and writes every talk. Safe to repeat.</summary>
    public async Task IndexAllAsync(IReadOnlyList<Session> sessions, CancellationToken cancellationToken)
    {
        var create = await SendAsync(Elastic.Transport.HttpMethod.PUT, $"/{IndexName}", Mapping, cancellationToken);
        if (!create.ApiCallDetails.HasSuccessfulStatusCode && !create.Body.Contains("resource_already_exists_exception"))
        {
            throw new TalkSearchException($"Couldn't create the talk index: {create.ApiCallDetails.HttpStatusCode}");
        }

        // Every talk needs an embedding, so don't redo them each time a pod starts.
        if (await CountAsync(cancellationToken) == sessions.Count)
        {
            return;
        }

        foreach (var batch in sessions.Chunk(BulkBatchSize))
        {
            var bulk = new StringBuilder();
            foreach (var session in batch)
            {
                bulk.Append($$$"""{"index":{"_id":"{{{session.Id}}}"}}""").Append('\n');
                bulk.Append(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["sessionId"] = session.Id,
                    ["title"] = session.Title,
                    ["semantic_content"] = SemanticText(session),
                })).Append('\n');
            }

            var written = await SendAsync(Elastic.Transport.HttpMethod.POST, $"/{IndexName}/_bulk?refresh=wait_for", bulk.ToString(), cancellationToken);
            if (!written.ApiCallDetails.HasSuccessfulStatusCode || written.Body.Contains("\"errors\":true"))
            {
                throw new TalkSearchException($"Couldn't index the talks: {written.ApiCallDetails.HttpStatusCode}");
            }
        }
    }

    private async Task<long> CountAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(Elastic.Transport.HttpMethod.POST, $"/{IndexName}/_count", """{"query":{"match_all":{}}}""", cancellationToken);
        if (!response.ApiCallDetails.HasSuccessfulStatusCode)
        {
            return -1;
        }

        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.TryGetProperty("count", out var count) ? count.GetInt64() : -1;
    }

    public Task<IReadOnlyList<int>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var text = JsonSerializer.Serialize(query);

        // Keywords catch exact names like "Argo"; the semantic half catches meaning.
        return SearchIdsAsync($$"""
            {
              "size": {{SearchResultCount}},
              "_source": ["sessionId"],
              "retriever": {
                "rrf": {
                  "rank_window_size": 20,
                  "retrievers": [
                    { "standard": { "query": { "multi_match": { "query": {{text}}, "fields": ["title"] } } } },
                    { "standard": { "query": { "semantic": { "field": "semantic_content", "query": {{text}} } } } }
                  ]
                }
              }
            }
            """, cancellationToken);
    }

    public Task<IReadOnlyList<int>> SimilarAsync(Session session, CancellationToken cancellationToken)
    {
        var text = JsonSerializer.Serialize(SemanticText(session));

        return SearchIdsAsync($$"""
            {
              "size": {{SimilarResultCount}},
              "_source": ["sessionId"],
              "query": {
                "bool": {
                  "must": { "semantic": { "field": "semantic_content", "query": {{text}} } },
                  "must_not": { "term": { "sessionId": {{session.Id}} } }
                }
              }
            }
            """, cancellationToken);
    }

    private async Task<IReadOnlyList<int>> SearchIdsAsync(string body, CancellationToken cancellationToken)
    {
        var response = await SendAsync(Elastic.Transport.HttpMethod.POST, $"/{IndexName}/_search", body, cancellationToken);
        if (!response.ApiCallDetails.HasSuccessfulStatusCode)
        {
            throw new TalkSearchException($"Talk search failed: {response.ApiCallDetails.HttpStatusCode}");
        }

        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray()
            .Select(hit => hit.GetProperty("_source").GetProperty("sessionId").GetInt32())
            .ToList();
    }

    private Task<StringResponse> SendAsync(
        Elastic.Transport.HttpMethod method,
        string path,
        string body,
        CancellationToken cancellationToken) =>
        client.Transport.RequestAsync<StringResponse>(new EndpointPath(method, path), PostData.String(body), cancellationToken: cancellationToken);

    private static string SemanticText(Session session) => session.Title;
}

/// <summary>For running without Elasticsearch: keywords only.</summary>
public sealed class LocalTalkSearch : ITalkSearch
{
    public Task<IReadOnlyList<int>> SearchAsync(string query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<int>>(Schedule.Filter(query).Select(session => session.Id).ToList());

    public Task<IReadOnlyList<int>> SimilarAsync(Session session, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<int>>(Schedule.Sessions.Where(other => other.Id != session.Id).Select(other => other.Id).ToList());
}

/// <summary>Writes the talks to Elasticsearch when a pod starts.</summary>
public sealed class TalkIndexer(ElasticTalkSearch search, ILogger<TalkIndexer> logger) : BackgroundService
{
    private const int Attempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                await search.IndexAllAsync(Schedule.Sessions, stoppingToken);
                logger.LogInformation("Indexed {Count} talks", Schedule.Sessions.Count);
                return;
            }
            catch (Exception e) when (e is TalkSearchException or HttpRequestException or TransportException)
            {
                // The embedding model can take a while to warm up on first use.
                logger.LogWarning(e, "Couldn't index talks on attempt {Attempt}", attempt);
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), stoppingToken);
            }
        }

        logger.LogError("Gave up indexing talks; search will use keywords only");
    }
}

/// <summary>A cap on embedding calls from the public search box, per pod.</summary>
public sealed class SearchBudget : IDisposable
{
    public const int SearchesPerMinute = 120;

    private readonly FixedWindowRateLimiter _limiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = SearchesPerMinute,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    public bool TryAcquire()
    {
        using var lease = _limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>Search and suggestions that fall back to keywords instead of failing.</summary>
public sealed class TalkFinder(
    ITalkSearch search,
    SearchBudget budget,
    IMemoryCache cache,
    ILogger<TalkFinder> logger)
{
    private const int MaxQueryLength = 200;
    private static readonly TimeSpan SearchCacheFor = TimeSpan.FromMinutes(5);

    // Talks don't change during the event, so what's similar doesn't either.
    private static readonly TimeSpan SimilarCacheFor = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<Session>> SearchAsync(string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Schedule.Sessions;
        }

        var text = query.Trim();
        text = text.Length > MaxQueryLength ? text[..MaxQueryLength] : text;
        var key = $"talks:search:{text.ToLowerInvariant()}";

        if (cache.TryGetValue(key, out IReadOnlyList<Session>? cached))
        {
            return cached!;
        }

        if (!budget.TryAcquire())
        {
            return Schedule.Filter(text);
        }

        try
        {
            var sessions = Resolve(await search.SearchAsync(text, cancellationToken));

            // No hits usually means the index is still being written, so don't cache it.
            if (sessions.Count == 0)
            {
                return Schedule.Filter(text);
            }

            cache.Set(key, sessions, SearchCacheFor);
            return sessions;
        }
        catch (Exception e) when (IsSearchFailure(e))
        {
            logger.LogError(e, "Talk search failed; using keywords");
            return Schedule.Filter(text);
        }
    }

    public async Task<IReadOnlyList<Session>> SimilarAsync(Session session, CancellationToken cancellationToken)
    {
        var key = $"talks:similar:{session.Id}";
        if (cache.TryGetValue(key, out IReadOnlyList<Session>? cached))
        {
            return cached!;
        }

        try
        {
            var sessions = Resolve(await search.SimilarAsync(session, cancellationToken));
            if (sessions.Count > 0)
            {
                cache.Set(key, sessions, SimilarCacheFor);
            }

            return sessions;
        }
        catch (Exception e) when (IsSearchFailure(e))
        {
            logger.LogError(e, "Couldn't find talks similar to session {Session}", session.Id);
            return [];
        }
    }

    private static IReadOnlyList<Session> Resolve(IReadOnlyList<int> ids) =>
        ids.Select(id => Schedule.Sessions.FirstOrDefault(session => session.Id == id)).OfType<Session>().ToList();

    private static bool IsSearchFailure(Exception e) =>
        e is TalkSearchException or HttpRequestException or TransportException or JsonException or KeyNotFoundException or InvalidOperationException;
}
