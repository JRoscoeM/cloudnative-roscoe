namespace App;

public sealed record Session(int Id, string Time, string Room, string Title);

/// <summary>Placeholder data until the real data lives in Elasticsearch.</summary>
public static class Schedule
{
    public static readonly IReadOnlyList<Session> Sessions =
    [
        new(1, "10:00", "Main", "Kickoff and sponsor talks"),
        new(2, "11:00", "Everywhere", "Build"),
        new(3, "14:00", "Main", "Panel"),
        new(4, "17:30", "Main", "Submissions close"),
    ];

    public static IReadOnlyList<Session> Filter(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? Sessions
            : Sessions.Where(s => s.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
}
