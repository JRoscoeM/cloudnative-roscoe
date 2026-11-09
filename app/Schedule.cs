namespace App;

public sealed record Session(int Id, string Time, string End, string RoomId, string Room, string Title, string Abstract)
{
    public DateTimeOffset StartsOn(DateOnly day) => Schedule.At(day, Time);

    public DateTimeOffset EndsOn(DateOnly day) => Schedule.At(day, End);
}

/// <summary>
/// Placeholder talks until the real schedule is available. Times are local to the venue.
/// </summary>
public static class Schedule
{
    // A room counts as still in use if its next talk starts within this gap.
    private static readonly TimeSpan ChangeoverGap = TimeSpan.FromMinutes(15);

    public static readonly IReadOnlyList<Session> Sessions =
    [
        new(1, "10:00", "11:00", "main-hall", "Main Hall", "Kickoff and sponsor talks",
            "Opening remarks, the rules of the day and short introductions from each sponsor."),
        new(2, "11:00", "14:00", "main-hall", "Main Hall", "Build",
            "Open hacking time. Mentors are on hand to help teams get unstuck."),
        new(3, "13:00", "14:00", "room-101", "Room 101", "Kubernetes on a budget",
            "Cut your cloud bill by rightsizing requests, using spot capacity and scaling idle workloads to zero."),
        new(4, "13:00", "14:00", "room-102", "Room 102", "Tracing without tears",
            "Add OpenTelemetry to a service and follow one slow request across every hop to find where the time went."),
        new(5, "14:00", "15:00", "room-101", "Room 101", "GitOps in practice",
            "Keep clusters in step with a Git repository using Argo CD, and recover from bad changes by reverting a commit."),
        new(6, "14:00", "15:00", "room-102", "Room 102", "Logs, metrics and sanity",
            "Build dashboards and alerts people trust, and stop paging the team for problems nobody can act on."),
        new(7, "14:00", "15:00", "main-hall", "Main Hall", "Panel",
            "Maintainers and platform engineers discuss what is changing in cloud native and what to ignore."),
        new(8, "15:00", "17:30", "main-hall", "Main Hall", "Build, continued",
            "The final stretch of hacking, with time set aside to prepare demos."),
        new(9, "15:00", "16:00", "room-101", "Room 101", "Rollouts you can trust",
            "Ship safely with canary releases and automated analysis, and roll back before users notice."),
        new(10, "15:00", "16:00", "room-102", "Room 102", "Search that understands you",
            "Combine keyword and vector retrieval so people find answers even when they use different words."),
        new(11, "16:00", "17:00", "room-101", "Room 101", "Securing your cluster",
            "Lock down workloads with least privilege, read-only filesystems, network policies and supply chain checks."),
        new(12, "16:00", "17:00", "room-102", "Room 102", "Agents in production",
            "Run AI agents reliably: tool design, evaluation, guardrails and what to monitor once real users arrive."),
        new(13, "17:30", "18:30", "main-hall", "Main Hall", "Submissions close",
            "Final submissions are due, followed by team demos and closing remarks."),
        new(14, "17:00", "18:00", "room-101", "Room 101", "Platform teams without burnout",
            "Treat your internal platform as a product, reduce toil and keep on-call humane."),
        new(15, "17:00", "18:00", "room-102", "Room 102", "Debugging with AI assistants",
            "Use AI investigations to narrow down production incidents, and check their conclusions against real evidence."),
    ];

    public static IReadOnlyList<Session> Filter(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? Sessions
            : Sessions.Where(s => s.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    public static DateOnly VenueDay(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(Venue.UtcOffset).DateTime);

    public static DateTimeOffset At(DateOnly day, string time) =>
        new(day.ToDateTime(TimeOnly.Parse(time)), Venue.UtcOffset);

    public static Session? Current(string roomId, DateTimeOffset now)
    {
        var day = VenueDay(now);
        return Sessions.LastOrDefault(s => s.RoomId == roomId && s.StartsOn(day) <= now && now < s.EndsOn(day));
    }

    public static Session? Next(string roomId, DateTimeOffset now)
    {
        var day = VenueDay(now);
        return Sessions.Where(s => s.RoomId == roomId && s.StartsOn(day) > now).MinBy(s => s.StartsOn(day));
    }

    /// <summary>
    /// When the room is next expected to empty, following back-to-back talks.
    /// Null when no talk is running there.
    /// </summary>
    public static DateTimeOffset? InUseUntil(string roomId, DateTimeOffset now)
    {
        var day = VenueDay(now);
        if (Current(roomId, now) is not { } session)
        {
            return null;
        }

        var end = session.EndsOn(day);
        foreach (var next in Sessions.Where(s => s.RoomId == roomId).OrderBy(s => s.StartsOn(day)))
        {
            if (next.StartsOn(day) >= session.StartsOn(day) && next.StartsOn(day) <= end + ChangeoverGap && next.EndsOn(day) > end)
            {
                end = next.EndsOn(day);
            }
        }

        return end;
    }
}
