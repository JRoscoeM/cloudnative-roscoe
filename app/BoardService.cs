using Microsoft.Extensions.Caching.Memory;

namespace App;

public sealed class BoardService(
    IReadingStore store,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<BoardService> logger)
{
    private const string CacheKey = "board";

    // Every open page polls, so share one database read across them for a moment.
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);

    public async Task<Board> GetAsync(CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            return BuildAsync(cancellationToken);
        }))!;

    private async Task<Board> BuildAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        try
        {
            var readings = await store.SinceAsync(now - OccupancyEstimator.SnapshotLifetime, cancellationToken);
            return BoardBuilder.Build(readings, now, store.IsSample);
        }
        catch (Exception e) when (e is ReadingStoreException or HttpRequestException or Elastic.Transport.TransportException)
        {
            logger.LogError(e, "Couldn't load readings for the board");
            return BoardBuilder.Build([], now, store.IsSample, dataUnavailable: true);
        }
    }
}
