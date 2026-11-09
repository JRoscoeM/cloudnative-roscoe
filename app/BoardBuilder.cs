namespace App;

/// <summary>Turns raw readings and the schedule into what the home page shows.</summary>
public static class BoardBuilder
{
    private static readonly TimeSpan NextTalkHorizon = TimeSpan.FromMinutes(15);

    public static Board Build(
        VenueSnapshot venue,
        IReadOnlyList<Reading> readings,
        DateTimeOffset now,
        bool isSample,
        bool dataUnavailable = false)
    {
        var byTarget = readings.ToLookup(reading => reading.Target);
        var layout = FloorPlan.Compute(venue);

        var rooms = venue.Rooms.Select(room => BuildRoom(room, byTarget[room.Id].ToList(), layout.Areas[room.Id], now)).ToList();
        var queues = venue.Queues.Select(queue => BuildQueue(queue, byTarget[queue.Id], layout.Areas[queue.Id], now)).ToList();

        return new Board(rooms, queues, isSample, dataUnavailable, layout.Height);
    }

    private static RoomStatus BuildRoom(RoomDefinition room, List<Reading> readings, MapArea area, DateTimeOffset now)
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
            area,
            NextUpText(room.Id, now),
            estimate is null ? null : OccupancyEstimator.Describe(estimate, now));
    }

    private static QueueStatus BuildQueue(QueueDefinition queue, IEnumerable<Reading> readings, MapArea marker, DateTimeOffset now)
    {
        var estimate = OccupancyEstimator.Estimate(readings, now);

        return new QueueStatus(
            queue.Id,
            queue.Name,
            queue.ShortName,
            estimate?.People,
            queue.SecondsPerPerson,
            marker,
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
