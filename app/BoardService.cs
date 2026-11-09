using Microsoft.Extensions.Caching.Memory;

namespace App;

public sealed class BoardService(
    IReadingStore store,
    TalkFinder talks,
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
            return await WithAlternativesAsync(BoardBuilder.Build(readings, now, store.IsSample), now, cancellationToken);
        }
        catch (Exception e) when (e is ReadingStoreException or HttpRequestException or Elastic.Transport.TransportException)
        {
            logger.LogError(e, "Couldn't load readings for the board");
            return BoardBuilder.Build([], now, store.IsSample, dataUnavailable: true);
        }
    }

    private async Task<Board> WithAlternativesAsync(Board board, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rooms = new List<RoomStatus>(board.Rooms.Count);
        foreach (var room in board.Rooms)
        {
            var current = room.Level is Busyness.Packed or Busyness.Full ? Schedule.Current(room.Id, now) : null;
            if (current is null)
            {
                rooms.Add(room);
                continue;
            }

            var similar = await talks.SimilarAsync(current, cancellationToken);
            rooms.Add(room with { Alternative = TalkSuggestions.Pick(room, similar, board.Rooms, now) });
        }

        return board with { Rooms = rooms };
    }
}
