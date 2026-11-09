namespace App;

public sealed record TalkAlternative(string Title, string RoomId, string RoomName, int Percent);

/// <summary>A talk as shown in search results, with how busy its room is.</summary>
public sealed record TalkResult(Session Session, string RoomName, string? Status, string StatusLevel);

public static class TalkSuggestions
{
    private const int ScheduleListLength = 30;
    private static readonly TimeSpan StartingSoon = TimeSpan.FromMinutes(15);

    /// <summary>The closest similar talk that's on now, or soon, in a room with space.</summary>
    public static TalkAlternative? Pick(
        RoomStatus room,
        IEnumerable<Session> similar,
        IReadOnlyList<RoomStatus> rooms,
        DateTimeOffset now)
    {
        foreach (var session in similar)
        {
            var other = RoomFor(session, rooms);
            if (other is { Level: Busyness.Open or Busyness.Filling } && other.Id != room.Id && IsOnNowOrSoon(session, now))
            {
                return new TalkAlternative(session.Title, other.Id, other.Name, other.Percent);
            }
        }

        return null;
    }

    /// <summary>What's coming up next. Hundreds of talks are too many for one page.</summary>
    public static IReadOnlyList<TalkResult> ForDisplay(IEnumerable<Session> sessions, Board board, DateTimeOffset now)
    {
        var ordered = sessions.OrderBy(session => session.Starts).ToList();
        var upcoming = ordered.Where(session => session.Ends > now).ToList();

        return (upcoming.Count > 0 ? upcoming : ordered)
            .Take(ScheduleListLength)
            .Select(session => Describe(session, board, now))
            .ToList();
    }

    /// <summary>Keeps relevance order, but things you can actually attend come first.</summary>
    public static IReadOnlyList<TalkResult> ForSearch(IEnumerable<Session> sessions, Board board, DateTimeOffset now) =>
        sessions
            .OrderBy(session => session.Ends <= now ? 2 : RoomFor(session, board.Rooms)?.Level == Busyness.Full ? 1 : 0)
            .Take(ScheduleListLength) // The keyword fallback can match most of the schedule.
            .Select(session => Describe(session, board, now))
            .ToList();

    private static RoomStatus? RoomFor(Session session, IReadOnlyList<RoomStatus> rooms) =>
        rooms.FirstOrDefault(room => room.ScheduleKey == session.RoomId);

    private static bool IsOnNowOrSoon(Session session, DateTimeOffset now) =>
        session.Starts <= now + StartingSoon && now < session.Ends;

    private static TalkResult Describe(Session session, Board board, DateTimeOffset now)
    {
        var room = RoomFor(session, board.Rooms);
        var roomName = room?.Name ?? session.Room;

        if (session.Ends <= now)
        {
            return new TalkResult(session, roomName, "Ended", "unknown");
        }

        return room is { HasReading: true }
            ? new TalkResult(session, roomName, $"{room.Level} · {room.Percent}%", room.Level.ToString().ToLowerInvariant())
            : new TalkResult(session, roomName, null, "unknown");
    }
}
