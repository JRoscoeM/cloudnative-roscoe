using System.Text.Json;
using System.Text.Json.Serialization;

namespace App;

/// <summary><paramref name="RoomId"/> is the slug of the room's name on the published schedule.</summary>
public sealed record Session(int Id, DateOnly Date, string Time, string End, string RoomId, string Room, string Title)
{
    [JsonIgnore]
    public DateTimeOffset Starts => At(Time);

    [JsonIgnore]
    public DateTimeOffset Ends => At(End) > Starts ? At(End) : At(End).AddDays(1); // Some evening events end at midnight.

    private DateTimeOffset At(string time) => new(Date.ToDateTime(TimeOnly.Parse(time)), Venue.UtcOffset);
}

/// <summary>
/// The KubeCon + CloudNativeCon North America 2026 schedule, from talks.json.
/// Times are local to the venue. Pass a list only in tests.
/// </summary>
public static class Schedule
{
    // A room counts as still in use if its next talk starts within this gap.
    private static readonly TimeSpan ChangeoverGap = TimeSpan.FromMinutes(15);

    public static readonly IReadOnlyList<Session> Sessions = Load();

    private static List<Session> Load()
    {
        using var stream = typeof(Schedule).Assembly.GetManifestResourceStream("talks.json")
            ?? throw new InvalidOperationException("talks.json is missing from the build.");
        return JsonSerializer.Deserialize<List<Session>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("talks.json is empty.");
    }

    public static IReadOnlyList<Session> Filter(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? Sessions
            : Sessions.Where(s => s.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    public static Session? Current(string roomKey, DateTimeOffset now, IReadOnlyList<Session>? sessions = null) =>
        (sessions ?? Sessions)
            .Where(s => s.RoomId == roomKey && s.Starts <= now && now < s.Ends)
            .MaxBy(s => s.Starts);

    public static Session? Next(string roomKey, DateTimeOffset now, IReadOnlyList<Session>? sessions = null) =>
        (sessions ?? Sessions)
            .Where(s => s.RoomId == roomKey && s.Starts > now)
            .MinBy(s => s.Starts);

    /// <summary>
    /// When the room is next expected to empty, following back-to-back talks.
    /// Null when no talk is running there.
    /// </summary>
    public static DateTimeOffset? InUseUntil(string roomKey, DateTimeOffset now, IReadOnlyList<Session>? sessions = null)
    {
        var all = sessions ?? Sessions;
        if (Current(roomKey, now, all) is not { } session)
        {
            return null;
        }

        var end = session.Ends;
        foreach (var next in all.Where(s => s.RoomId == roomKey).OrderBy(s => s.Starts))
        {
            if (next.Starts >= session.Starts && next.Starts <= end + ChangeoverGap && next.Ends > end)
            {
                end = next.Ends;
            }
        }

        return end;
    }
}
