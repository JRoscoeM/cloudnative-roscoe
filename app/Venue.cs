namespace App;

public sealed record RoomDefinition(string Id, string Name, int Capacity, MapArea Area);

/// <summary><paramref name="SecondsPerPerson"/> is hand-timed at each queue.</summary>
public sealed record QueueDefinition(
    string Id,
    string Name,
    string ShortName,
    int SecondsPerPerson,
    MapArea Marker);

/// <summary>
/// The spaces the app knows about. These are placeholders: replace them with the
/// venue's real rooms, capacities and layout.
/// </summary>
public static class Venue
{
    // A fixed offset because the runtime image has no time zone database, and
    // the event is a single day in Mountain Standard Time.
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(-7);

    public static readonly IReadOnlyList<RoomDefinition> Rooms =
    [
        new("main-hall", "Main Hall", 20, new(8, 8, 344, 112)),
        new("room-101", "Room 101", 5, new(8, 128, 168, 96)),
        new("room-102", "Room 102", 10, new(184, 128, 168, 96)),
    ];

    public static readonly IReadOnlyList<QueueDefinition> Queues =
    [
        new("coffee", "Coffee bar", "Coffee", 20, new(8, 240, 110, 40)),
        new("lunch", "Lunch line", "Lunch", 25, new(125, 240, 110, 40)),
        new("badges", "Badge pickup", "Badges", 30, new(242, 240, 110, 40)),
    ];

    public static RoomDefinition? FindRoom(string? id) => Rooms.FirstOrDefault(room => room.Id == id);

    public static bool IsKnownTarget(string? id) =>
        Rooms.Any(room => room.Id == id) || Queues.Any(queue => queue.Id == id);

    /// <summary>The largest count a target can plausibly report.</summary>
    public static int MaxPeople(string id) => FindRoom(id) is { } room ? room.Capacity * 2 : 500;
}
