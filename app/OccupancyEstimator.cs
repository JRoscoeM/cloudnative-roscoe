namespace App;

/// <summary>Merges sensor and snapshot readings into one count for a space.</summary>
public static class OccupancyEstimator
{
    public static readonly TimeSpan SensorFreshFor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A live sensor wins. A recent snapshot can only pull a sensor's count up,
    /// because detectors undercount crowds, and it counts for less as it ages.
    /// With no live sensor, the latest unexpired snapshot stands in.
    /// </summary>
    public static Estimate? Estimate(IEnumerable<Reading> readings, DateTimeOffset now)
    {
        var ordered = readings.OrderByDescending(reading => reading.CapturedAt).ToList();
        var sensor = ordered.FirstOrDefault(reading =>
            reading.Source == ReadingSource.Sensor && now - reading.CapturedAt <= SensorFreshFor);
        var snapshot = ordered.FirstOrDefault(reading =>
            reading.Source == ReadingSource.Snapshot && now - reading.CapturedAt <= SnapshotLifetime);

        if (sensor is null)
        {
            return snapshot is null ? null : new Estimate(snapshot.People, ReadingSource.Snapshot, snapshot.CapturedAt);
        }

        if (snapshot is null || snapshot.People <= sensor.People)
        {
            return new Estimate(sensor.People, ReadingSource.Sensor, sensor.CapturedAt);
        }

        var corrected = sensor.People + SnapshotWeight(snapshot, now) * (snapshot.People - sensor.People);
        return new Estimate((int)Math.Round(corrected), ReadingSource.Sensor, sensor.CapturedAt);
    }

    public static double SnapshotWeight(Reading snapshot, DateTimeOffset now)
    {
        var remaining = 1 - ((now - snapshot.CapturedAt) / SnapshotLifetime);
        return Math.Clamp(snapshot.Confidence * remaining, 0, 1);
    }

    public static string Describe(Estimate estimate, DateTimeOffset now)
    {
        var minutes = (int)(now - estimate.AsOf).TotalMinutes;
        var age = minutes < 1 ? "just now" : $"{minutes} min ago";
        return estimate.Source == ReadingSource.Sensor ? $"Sensor, {age}" : $"Photo, {age}";
    }
}
