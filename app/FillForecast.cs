namespace App;

/// <summary>
/// Projects when a room fills from its recent trend and the schedule. There is
/// no history to train on, so this is a straight-line fit.
/// </summary>
public static class FillForecast
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(1);
    private const int MinimumPoints = 3;
    private const double MinimumPeoplePerMinute = 0.25;
    private const int LongestForecastMinutes = 120;

    /// <summary>Whole minutes until full, or null when the room isn't on course to fill.</summary>
    public static int? MinutesToFull(
        IEnumerable<Reading> readings,
        int currentPeople,
        int capacity,
        DateTimeOffset now,
        DateTimeOffset? inUseUntil)
    {
        if (currentPeople >= capacity)
        {
            return null;
        }

        var slope = PeoplePerMinute(readings, now);
        if (slope is null || slope < MinimumPeoplePerMinute)
        {
            return null;
        }

        var minutes = (int)Math.Ceiling((capacity - currentPeople) / slope.Value);
        if (minutes > LongestForecastMinutes)
        {
            return null;
        }

        // People leave when the talks end, so a fill time after that won't happen.
        return inUseUntil is { } end && now.AddMinutes(minutes) > end ? null : Math.Max(1, minutes);
    }

    /// <summary>Least-squares slope, or null with too little data to fit.</summary>
    public static double? PeoplePerMinute(IEnumerable<Reading> readings, DateTimeOffset now)
    {
        var points = readings
            .Where(reading => reading.Source == ReadingSource.Sensor && now - reading.CapturedAt <= Window)
            .ToList();

        if (points.Count < MinimumPoints)
        {
            return null;
        }

        var earliest = points.Min(point => point.CapturedAt);
        var span = points.Max(point => point.CapturedAt) - earliest;
        if (span < MinimumSpan)
        {
            return null;
        }

        var xs = points.Select(point => (point.CapturedAt - earliest).TotalMinutes).ToList();
        var ys = points.Select(point => (double)point.People).ToList();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var denominator = xs.Sum(x => (x - meanX) * (x - meanX));
        return denominator == 0 ? null : xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum() / denominator;
    }
}
