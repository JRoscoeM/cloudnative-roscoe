namespace App;

/// <summary>Turns raw readings and the schedule into what the home page shows.</summary>
public static class BoardBuilder
{
    private static readonly TimeSpan NextTalkHorizon = TimeSpan.FromMinutes(15);

    public static Board Build(IReadOnlyList<Reading> readings, DateTimeOffset now, bool isSample, bool dataUnavailable = false)
    {
        var byTarget = readings.ToLookup(reading => reading.Target);

        var rooms = Venue.Rooms.Select(room => BuildRoom(room, byTarget[room.Id].ToList(), now)).ToList();
        var queues = Venue.Queues.Select(queue => BuildQueue(queue, byTarget[queue.Id], now)).ToList();

        return new Board(rooms, queues, isSample, dataUnavailable);
    }

    private static RoomStatus BuildRoom(RoomDefinition room, List<Reading> readings, DateTimeOffset now)
    {
        var estimate = OccupancyEstimator.Estimate(readings, now);
        var minutesToFull = estimate is null
            ? null
            : FillForecast.MinutesToFull(readings, estimate.People, room.Capacity, now, Schedule.InUseUntil(room.Id, now));

        return new RoomStatus(
            room.Id,
            room.Name,
            Schedule.Current(room.Id, now)?.Title,
            room.Capacity,
            estimate?.People,
            minutesToFull,
            room.Area,
            NextUpText(room.Id, now),
            estimate is null ? null : OccupancyEstimator.Describe(estimate, now));
    }

    private static QueueStatus BuildQueue(QueueDefinition queue, IEnumerable<Reading> readings, DateTimeOffset now)
    {
        var estimate = OccupancyEstimator.Estimate(readings, now);

        return new QueueStatus(
            queue.Id,
            queue.Name,
            queue.ShortName,
            estimate?.People,
            queue.SecondsPerPerson,
            queue.Marker,
            estimate is null ? null : OccupancyEstimator.Describe(estimate, now));
    }

    private static string? NextUpText(string roomId, DateTimeOffset now)
    {
        if (Schedule.Next(roomId, now) is not { } next)
        {
            return null;
        }

        var startsIn = next.StartsOn(Schedule.VenueDay(now)) - now;
        return startsIn <= NextTalkHorizon ? $"Next talk in {Math.Max(1, (int)startsIn.TotalMinutes)} min" : null;
    }
}
