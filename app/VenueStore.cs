using System.Text;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Mapping;
using Microsoft.Extensions.Caching.Memory;

namespace App;

public interface IVenueStore
{
    Task<VenueSnapshot> LoadAsync(CancellationToken cancellationToken);

    Task SaveRoomAsync(RoomDefinition room, CancellationToken cancellationToken);

    Task SaveQueueAsync(QueueDefinition queue, CancellationToken cancellationToken);

    Task DeleteAsync(string id, CancellationToken cancellationToken);
}

public sealed class VenueStoreException(string message) : Exception(message);

/// <summary>Rooms and queues in Elasticsearch, shared by every replica.</summary>
public sealed class ElasticVenueStore(ElasticsearchClient client) : IVenueStore
{
    public const string IndexName = "headroom-venue";

    private const string RoomKind = "room";
    private const string QueueKind = "queue";

    private volatile bool _indexReady;

    public async Task<VenueSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken);

        var response = await client.SearchAsync<VenueDocument>(
            new SearchRequest<VenueDocument>(IndexName) { Size = 500 },
            cancellationToken);
        if (!response.IsValidResponse)
        {
            throw new VenueStoreException($"Couldn't load the venue: {response.ElasticsearchServerError?.Error?.Type}");
        }

        var documents = response.Documents.ToList();

        // The dashboard refuses to delete the last room, so no rooms means a new venue.
        if (documents.All(document => document.Kind != RoomKind))
        {
            await SeedAsync(cancellationToken);
            return Venue.Default;
        }

        return new VenueSnapshot(
            documents.Where(d => d.Kind == RoomKind).Select(d => new RoomDefinition(d.Id, d.Name, d.Capacity)).ToList(),
            documents.Where(d => d.Kind == QueueKind).Select(d => new QueueDefinition(d.Id, d.Name, d.ShortName, d.SecondsPerPerson)).ToList());
    }

    public Task SaveRoomAsync(RoomDefinition room, CancellationToken cancellationToken) =>
        WriteAsync(new VenueDocument { Kind = RoomKind, Id = room.Id, Name = room.Name, Capacity = room.Capacity }, cancellationToken);

    public Task SaveQueueAsync(QueueDefinition queue, CancellationToken cancellationToken) =>
        WriteAsync(
            new VenueDocument { Kind = QueueKind, Id = queue.Id, Name = queue.Name, ShortName = queue.ShortName, SecondsPerPerson = queue.SecondsPerPerson },
            cancellationToken);

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var response = await client.DeleteAsync<VenueDocument>(
            id,
            request => request.Index(IndexName).Refresh(Refresh.WaitFor),
            cancellationToken);

        if (!response.IsValidResponse && response.ApiCallDetails.HttpStatusCode != 404)
        {
            throw new VenueStoreException($"Couldn't delete {id}: {response.ElasticsearchServerError?.Error?.Type}");
        }
    }

    private async Task WriteAsync(VenueDocument document, CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken);

        var response = await client.IndexAsync(
            document,
            request => request.Index(IndexName).Id(document.Id).Refresh(Refresh.WaitFor),
            cancellationToken);
        if (!response.IsValidResponse)
        {
            throw new VenueStoreException($"Couldn't save {document.Id}: {response.ElasticsearchServerError?.Error?.Type}");
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        // Fixed ids, so both replicas seeding at once write the same documents.
        foreach (var room in Venue.Default.Rooms)
        {
            await SaveRoomAsync(room, cancellationToken);
        }

        foreach (var queue in Venue.Default.Queues)
        {
            await SaveQueueAsync(queue, cancellationToken);
        }
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
                    { "kind", new KeywordProperty() },
                    { "id", new KeywordProperty() },
                    { "name", new TextProperty() },
                    { "shortName", new KeywordProperty() },
                    { "capacity", new IntegerNumberProperty() },
                    { "secondsPerPerson", new IntegerNumberProperty() },
                },
            },
        };

        var response = await client.Indices.CreateAsync(request, cancellationToken);
        if (!response.IsValidResponse && response.ElasticsearchServerError?.Error?.Type != "resource_already_exists_exception")
        {
            throw new VenueStoreException($"Couldn't create the venue index: {response.ElasticsearchServerError?.Error?.Type}");
        }

        _indexReady = true;
    }

    // Internal, not private: the client's JSON serializer has to reach it.
    internal sealed class VenueDocument
    {
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ShortName { get; set; } = "";
        public int Capacity { get; set; }
        public int SecondsPerPerson { get; set; }
    }
}

/// <summary>For running without Elasticsearch, on a laptop or in tests.</summary>
public sealed class LocalVenueStore : IVenueStore
{
    private readonly Lock _gate = new();
    private readonly List<RoomDefinition> _rooms = [.. Venue.Default.Rooms];
    private readonly List<QueueDefinition> _queues = [.. Venue.Default.Queues];

    public Task<VenueSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(new VenueSnapshot([.. _rooms], [.. _queues]));
        }
    }

    public Task SaveRoomAsync(RoomDefinition room, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _rooms.RemoveAll(existing => existing.Id == room.Id);
            _rooms.Add(room);
        }

        return Task.CompletedTask;
    }

    public Task SaveQueueAsync(QueueDefinition queue, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _queues.RemoveAll(existing => existing.Id == queue.Id);
            _queues.Add(queue);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _rooms.RemoveAll(existing => existing.Id == id);
            _queues.RemoveAll(existing => existing.Id == id);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The current venue, cached briefly and never blank.</summary>
public sealed class VenueService(IVenueStore store, IMemoryCache cache, ILogger<VenueService> logger)
{
    private const string CacheKey = "venue";

    // How long the other replica can show an old room name after an edit.
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private VenueSnapshot _lastKnown = Venue.Default;

    public async Task<VenueSnapshot> GetAsync(CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            try
            {
                var venue = Sorted(await store.LoadAsync(cancellationToken));
                Volatile.Write(ref _lastKnown, venue);
                return venue;
            }
            catch (Exception e) when (e is VenueStoreException or HttpRequestException or Elastic.Transport.TransportException)
            {
                logger.LogError(e, "Couldn't load the venue; using the last known version");
                return Volatile.Read(ref _lastKnown);
            }
        }))!;

    /// <summary>
    /// Skips the cache, because the other replica may have just changed the
    /// venue. Use before an edit. Null when the store can't be reached, so an
    /// edit is never checked against old data.
    /// </summary>
    public async Task<VenueSnapshot?> GetFreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var venue = Sorted(await store.LoadAsync(cancellationToken));
            Volatile.Write(ref _lastKnown, venue);
            cache.Set(CacheKey, venue, CacheFor);
            return venue;
        }
        catch (Exception e) when (e is VenueStoreException or HttpRequestException or Elastic.Transport.TransportException)
        {
            logger.LogError(e, "Couldn't load the venue for an edit");
            return null;
        }
    }

    public async Task SaveRoomAsync(RoomDefinition room, CancellationToken cancellationToken)
    {
        await store.SaveRoomAsync(room, cancellationToken);
        cache.Remove(CacheKey);
    }

    public async Task SaveQueueAsync(QueueDefinition queue, CancellationToken cancellationToken)
    {
        await store.SaveQueueAsync(queue, cancellationToken);
        cache.Remove(CacheKey);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await store.DeleteAsync(id, cancellationToken);
        cache.Remove(CacheKey);
    }

    // Largest first, because the floor plan gives the first room the full width.
    private static VenueSnapshot Sorted(VenueSnapshot venue) => new(
        venue.Rooms.OrderByDescending(room => room.Capacity).ThenBy(room => room.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        venue.Queues.OrderBy(queue => queue.Name, StringComparer.OrdinalIgnoreCase).ToList());
}

public static class VenueIds
{
    /// <summary>A URL-safe id from a name, kept unique across rooms and queues.</summary>
    public static string New(string name, VenueSnapshot venue)
    {
        var slug = new StringBuilder();
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var baseId = slug.ToString().Trim('-');
        baseId = baseId.Length == 0 ? "space" : baseId;

        var id = baseId;
        for (var suffix = 2; venue.IsKnownTarget(id); suffix++)
        {
            id = $"{baseId}-{suffix}";
        }

        return id;
    }
}
