namespace App;

public sealed record RoomDefinition(string Id, string Name, int Capacity);

/// <summary><paramref name="SecondsPerPerson"/> is hand-timed at each queue.</summary>
public sealed record QueueDefinition(string Id, string Name, string ShortName, int SecondsPerPerson);

/// <summary>The rooms and queues as they stand at one moment.</summary>
public sealed record VenueSnapshot(IReadOnlyList<RoomDefinition> Rooms, IReadOnlyList<QueueDefinition> Queues)
{
    public RoomDefinition? FindRoom(string? id) => Rooms.FirstOrDefault(room => room.Id == id);

    public QueueDefinition? FindQueue(string? id) => Queues.FirstOrDefault(queue => queue.Id == id);

    public bool IsKnownTarget(string? id) => FindRoom(id) is not null || FindQueue(id) is not null;

    /// <summary>The largest count a target can plausibly report.</summary>
    public int MaxPeople(string id) => FindRoom(id) is { } room ? room.Capacity * 2 : 500;
}

/// <summary>
/// What a new venue starts with. Organizers change it from the dashboard, and
/// their changes are stored in Elasticsearch.
/// </summary>
public static class Venue
{
    // A fixed offset because the runtime image has no time zone database, and
    // the event is a single day in Mountain Standard Time.
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(-7);

    public static readonly VenueSnapshot Default = new(
        Rooms:
        [
            new("main-hall", "Main Hall", 20),
            new("room-101", "Room 101", 5),
            new("room-102", "Room 102", 10),
        ],
        Queues:
        [
            new("coffee", "Coffee bar", "Coffee", 20),
            new("lunch", "Lunch line", "Lunch", 25),
            new("badges", "Badge pickup", "Badges", 30),
        ]);
}
