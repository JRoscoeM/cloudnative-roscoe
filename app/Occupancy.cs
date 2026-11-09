namespace App;

public enum Busyness
{
    Unknown,
    Open,
    Filling,
    Packed,
    Full,
}

public enum ReadingSource
{
    Sensor,
    Snapshot,
}

/// <summary>Where something sits on the floor plan, in SVG units.</summary>
public sealed record MapArea(int X, int Y, int Width, int Height);

/// <summary>One count for a room or queue. Holds numbers only, never an image.</summary>
public sealed record Reading(
    string Target,
    ReadingSource Source,
    int People,
    double Confidence,
    DateTimeOffset CapturedAt);

public sealed record Estimate(int People, ReadingSource Source, DateTimeOffset AsOf);

public sealed record RoomStatus(
    string Id,
    string Name,
    string? NowShowing,
    int Capacity,
    int? People,
    int? MinutesToFull,
    MapArea Area,
    string? NextUp = null,
    string? Freshness = null,
    TalkAlternative? Alternative = null)
{
    public bool HasReading => People is not null;

    // Rounds down, so a room only reads as 100% once it is actually full.
    public int Percent => Capacity <= 0 || People is not int people ? 0 : Math.Min(100, 100 * people / Capacity);

    public Busyness Level => !HasReading
        ? Busyness.Unknown
        : Percent switch
        {
            >= 100 => Busyness.Full,
            >= 85 => Busyness.Packed,
            >= 60 => Busyness.Filling,
            _ => Busyness.Open,
        };

    public string Outlook => Level switch
    {
        Busyness.Unknown => "No reading yet",
        Busyness.Full => "At capacity",
        _ when MinutesToFull is int minutes => $"Full in about {minutes} min",
        _ when NextUp is not null => NextUp,
        _ => "Holding steady",
    };

    public string PercentText => HasReading ? $"{Percent}%" : "–";

    public string TalkText => NowShowing ?? "No session right now";
}

public sealed record QueueStatus(
    string Id,
    string Name,
    string ShortName,
    int? People,
    int SecondsPerPerson,
    MapArea Marker,
    string? Freshness = null)
{
    public int? WaitMinutes => People is int people ? (int)Math.Ceiling(people * SecondsPerPerson / 60d) : null;

    public string WaitText => WaitMinutes switch
    {
        null => "No data",
        0 => "No wait",
        var minutes => $"{minutes} min",
    };

    public string PeopleText => People is int people ? $"{people} in line" : "Waiting for a sensor";
}

/// <summary>Everything the home page shows about the venue at one moment.</summary>
public sealed record Board(
    IReadOnlyList<RoomStatus> Rooms,
    IReadOnlyList<QueueStatus> Queues,
    bool IsSample,
    bool DataUnavailable = false)
{
    // Rooms with no reading sort last: they can't be recommended yet.
    public IEnumerable<RoomStatus> RoomsWithMostSpaceFirst => Rooms
        .OrderBy(room => room.HasReading ? 0 : 1)
        .ThenBy(room => room.Percent);

    public RoomStatus? MostSpace => Rooms
        .Where(room => room.HasReading && room.Level != Busyness.Full)
        .MinBy(room => room.Percent);

    public RoomStatus? FillingFastest => Rooms
        .Where(room => room.HasReading && room.Level != Busyness.Full && room.MinutesToFull is not null)
        .MinBy(room => room.MinutesToFull);

    public QueueStatus? ShortestLine => Queues
        .Where(queue => queue.WaitMinutes is not null)
        .MinBy(queue => queue.WaitMinutes);

    public bool HasAnyReading => Rooms.Any(room => room.HasReading) || Queues.Any(queue => queue.People is not null);
}
