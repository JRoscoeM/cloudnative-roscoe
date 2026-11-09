namespace App;

public sealed record TalkAlternative(string Title, string RoomId, string RoomName, int Percent);

/// <summary>A talk as shown in search results, with how busy its room is.</summary>
public sealed record TalkResult(Session Session, string? Status, string StatusLevel);

public static class TalkSuggestions
{
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
            var other = rooms.FirstOrDefault(candidate => candidate.Id == session.RoomId);
            if (other is { Level: Busyness.Open or Busyness.Filling } && other.Id != room.Id && IsOnNowOrSoon(session, now))
            {
                return new TalkAlternative(session.Title, other.Id, other.Name, other.Percent);
            }
        }

        return null;
    }

    public static IReadOnlyList<TalkResult> ForDisplay(IEnumerable<Session> sessions, Board board, DateTimeOffset now) =>
        sessions.OrderBy(session => session.Time).Select(session => Describe(session, board, now)).ToList();

    /// <summary>Keeps relevance order, but things you can actually attend come first.</summary>
    public static IReadOnlyList<TalkResult> ForSearch(IEnumerable<Session> sessions, Board board, DateTimeOffset now)
    {
        var day = Schedule.VenueDay(now);
        return sessions
            .OrderBy(session => session.EndsOn(day) <= now ? 2 : RoomIsFull(session, board) ? 1 : 0)
            .Select(session => Describe(session, board, now))
            .ToList();
    }

    private static bool RoomIsFull(Session session, Board board) =>
        board.Rooms.FirstOrDefault(room => room.Id == session.RoomId)?.Level == Busyness.Full;

    private static bool IsOnNowOrSoon(Session session, DateTimeOffset now)
    {
        var day = Schedule.VenueDay(now);
        return session.StartsOn(day) <= now + StartingSoon && now < session.EndsOn(day);
    }

    private static TalkResult Describe(Session session, Board board, DateTimeOffset now)
    {
        if (session.EndsOn(Schedule.VenueDay(now)) <= now)
        {
            return new TalkResult(session, "Ended", "unknown");
        }

        var room = board.Rooms.FirstOrDefault(candidate => candidate.Id == session.RoomId);
        return room is { HasReading: true }
            ? new TalkResult(session, $"{room.Level} · {room.Percent}%", room.Level.ToString().ToLowerInvariant())
            : new TalkResult(session, null, "unknown");
    }
}
