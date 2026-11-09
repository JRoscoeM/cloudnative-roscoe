using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace App;

/// <summary>
/// A key unlocks one organizer token. ORGANIZER_KEY if set, otherwise SENSOR_KEY,
/// so a venue can start with one key and separate them later.
/// </summary>
public sealed class OrganizerAuth(string? key)
{
    private const string TokenScope = "organizer:dashboard";

    private readonly SensorAuth _keys = new(key);

    public bool Enabled => _keys.Enabled;

    public bool IsKey(string? candidate) => _keys.IsSetupKey(candidate);

    public string Token() => _keys.TokenFor(TokenScope);

    public bool IsValidToken(string? token) => _keys.IsValidToken(TokenScope, token);
}

public sealed record OrganizerSessionRequest(string? Key);

public sealed record RoomRequest(string? Name, int Capacity);

public sealed record QueueRequest(string? Name, int SecondsPerPerson);

public static class OrganizerApi
{
    public const string SessionPolicy = "organizer-session";
    public const string ApiPolicy = "organizer-api";

    private const int MaxNameLength = 40;
    private const int MaxCapacity = 100_000;
    private const int MaxSecondsPerPerson = 3600;
    private const int ShortNameLength = 10;

    public static void AddOrganizerRateLimits(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options)
    {
        options.AddPolicy(SessionPolicy, context => Window(OccupancyApi.ClientKey(context), 10));
        options.AddPolicy(ApiPolicy, context => Window(OccupancyApi.BearerToken(context) ?? OccupancyApi.ClientKey(context), 120));
    }

    private static RateLimitPartition<string> Window(string key, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });

    public static void MapOrganizerApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/organizer/session", CreateSession).RequireRateLimiting(SessionPolicy);

        var api = app.MapGroup("/api/organizer")
            .RequireRateLimiting(ApiPolicy)
            .AddEndpointFilter(async (context, next) =>
            {
                var auth = context.HttpContext.RequestServices.GetRequiredService<OrganizerAuth>();
                return auth.IsValidToken(OccupancyApi.BearerToken(context.HttpContext))
                    ? await next(context)
                    : Error(StatusCodes.Status401Unauthorized, "Please sign in again.");
            });

        api.MapGet("/overview", GetOverview);
        api.MapPost("/rooms", AddRoom);
        api.MapPut("/rooms/{id}", UpdateRoom);
        api.MapDelete("/rooms/{id}", DeleteRoom);
        api.MapPost("/queues", AddQueue);
        api.MapPut("/queues/{id}", UpdateQueue);
        api.MapDelete("/queues/{id}", DeleteQueue);
        api.MapPost("/sensor-links/{id}", CreateSensorLink);
    }

    private static IResult CreateSession(OrganizerSessionRequest request, OrganizerAuth auth)
    {
        if (!auth.Enabled)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "The organizer key isn't set up yet.");
        }

        return auth.IsKey(request.Key)
            ? Results.Json(new { token = auth.Token() })
            : Error(StatusCodes.Status401Unauthorized, "That key isn't right.");
    }

    private static async Task<IResult> GetOverview(
        VenueService venues,
        IReadingStore store,
        SiteHealthService health,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var venue = await venues.GetAsync(cancellationToken);
        var readings = await TryReadAsync(store, now, cancellationToken);
        var report = await health.GetAsync(cancellationToken);

        return Results.Json(new
        {
            isSample = store.IsSample,
            health = report,
            rooms = venue.Rooms.Select(room => new
            {
                room.Id,
                room.Name,
                room.Capacity,
                sensor = SensorState.Describe(room.Id, readings, now),
            }),
            queues = venue.Queues.Select(queue => new
            {
                queue.Id,
                queue.Name,
                queue.SecondsPerPerson,
                sensor = SensorState.Describe(queue.Id, readings, now),
            }),
        });
    }

    private static async Task<IReadOnlyList<Reading>?> TryReadAsync(IReadingStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await store.SinceAsync(now - OccupancyEstimator.SnapshotLifetime, cancellationToken);
        }
        catch (Exception e) when (e is ReadingStoreException or HttpRequestException or Elastic.Transport.TransportException)
        {
            return null;
        }
    }

    private static async Task<IResult> AddRoom(RoomRequest request, VenueService venues, CancellationToken cancellationToken)
    {
        if (ValidateRoom(request) is { } problem)
        {
            return problem;
        }

        var venue = await venues.GetAsync(cancellationToken);
        var room = new RoomDefinition(VenueIds.New(request.Name!, venue), request.Name!.Trim(), request.Capacity);
        return await SaveAsync(() => venues.SaveRoomAsync(room, cancellationToken), room);
    }

    private static async Task<IResult> UpdateRoom(string id, RoomRequest request, VenueService venues, CancellationToken cancellationToken)
    {
        if (ValidateRoom(request) is { } problem)
        {
            return problem;
        }

        var venue = await venues.GetAsync(cancellationToken);
        if (venue.FindRoom(id) is null)
        {
            return Error(StatusCodes.Status404NotFound, "That room no longer exists.");
        }

        var room = new RoomDefinition(id, request.Name!.Trim(), request.Capacity);
        return await SaveAsync(() => venues.SaveRoomAsync(room, cancellationToken), room);
    }

    private static async Task<IResult> DeleteRoom(string id, VenueService venues, CancellationToken cancellationToken)
    {
        var venue = await venues.GetAsync(cancellationToken);
        if (venue.FindRoom(id) is null)
        {
            return Error(StatusCodes.Status404NotFound, "That room no longer exists.");
        }

        if (venue.Rooms.Count == 1)
        {
            return Error(StatusCodes.Status400BadRequest, "A venue needs at least one room.");
        }

        return await SaveAsync(() => venues.DeleteAsync(id, cancellationToken), new { id });
    }

    private static async Task<IResult> AddQueue(QueueRequest request, VenueService venues, CancellationToken cancellationToken)
    {
        if (ValidateQueue(request) is { } problem)
        {
            return problem;
        }

        var venue = await venues.GetAsync(cancellationToken);
        var queue = ToQueue(VenueIds.New(request.Name!, venue), request);
        return await SaveAsync(() => venues.SaveQueueAsync(queue, cancellationToken), queue);
    }

    private static async Task<IResult> UpdateQueue(string id, QueueRequest request, VenueService venues, CancellationToken cancellationToken)
    {
        if (ValidateQueue(request) is { } problem)
        {
            return problem;
        }

        var venue = await venues.GetAsync(cancellationToken);
        if (venue.FindQueue(id) is null)
        {
            return Error(StatusCodes.Status404NotFound, "That line no longer exists.");
        }

        var queue = ToQueue(id, request);
        return await SaveAsync(() => venues.SaveQueueAsync(queue, cancellationToken), queue);
    }

    private static async Task<IResult> DeleteQueue(string id, VenueService venues, CancellationToken cancellationToken)
    {
        var venue = await venues.GetAsync(cancellationToken);
        return venue.FindQueue(id) is null
            ? Error(StatusCodes.Status404NotFound, "That line no longer exists.")
            : await SaveAsync(() => venues.DeleteAsync(id, cancellationToken), new { id });
    }

    private static async Task<IResult> CreateSensorLink(string id, VenueService venues, SensorAuth sensors, CancellationToken cancellationToken)
    {
        if (!sensors.Enabled)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "Add SENSOR_KEY before setting up sensors.");
        }

        var venue = await venues.GetAsync(cancellationToken);
        if (!venue.IsKnownTarget(id))
        {
            return Error(StatusCodes.Status404NotFound, "That space no longer exists.");
        }

        // In the fragment, so the token never reaches a server log.
        return Results.Json(new { path = $"/sensor#target={Uri.EscapeDataString(id)}&token={sensors.TokenFor(id)}" });
    }

    private static async Task<IResult> SaveAsync(Func<Task> save, object result)
    {
        try
        {
            await save();
            return Results.Json(result);
        }
        catch (Exception e) when (e is VenueStoreException or HttpRequestException or Elastic.Transport.TransportException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "That change couldn't be saved. Please try again.");
        }
    }

    private static QueueDefinition ToQueue(string id, QueueRequest request)
    {
        var name = request.Name!.Trim();
        var firstWord = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var shortName = firstWord.Length > ShortNameLength ? firstWord[..ShortNameLength] : firstWord;
        return new QueueDefinition(id, name, shortName, request.SecondsPerPerson);
    }

    private static IResult? ValidateRoom(RoomRequest request)
    {
        if (!IsValidName(request.Name))
        {
            return Error(StatusCodes.Status400BadRequest, $"A name needs 1 to {MaxNameLength} characters.");
        }

        return request.Capacity is < 1 or > MaxCapacity
            ? Error(StatusCodes.Status400BadRequest, $"Capacity must be between 1 and {MaxCapacity}.")
            : null;
    }

    private static IResult? ValidateQueue(QueueRequest request)
    {
        if (!IsValidName(request.Name))
        {
            return Error(StatusCodes.Status400BadRequest, $"A name needs 1 to {MaxNameLength} characters.");
        }

        return request.SecondsPerPerson is < 1 or > MaxSecondsPerPerson
            ? Error(StatusCodes.Status400BadRequest, $"Seconds per person must be between 1 and {MaxSecondsPerPerson}.")
            : null;
    }

    private static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength;

    private static IResult Error(int status, string message) =>
        Results.Json(new { error = message }, statusCode: status);
}

public static class SensorState
{
    /// <summary>Whether a space has a live sensor. Null readings mean the store is unavailable.</summary>
    public static object Describe(string target, IReadOnlyList<Reading>? readings, DateTimeOffset now)
    {
        if (readings is null)
        {
            return new { state = "unknown", detail = "Couldn't check" };
        }

        var latest = readings
            .Where(reading => reading.Target == target && reading.Source == ReadingSource.Sensor)
            .MaxBy(reading => reading.CapturedAt);

        if (latest is null)
        {
            return new { state = "none", detail = "No sensor" };
        }

        var age = now - latest.CapturedAt;
        var ago = age.TotalMinutes < 1 ? "just now" : $"{(int)age.TotalMinutes} min ago";
        return age <= OccupancyEstimator.SensorFreshFor
            ? new { state = "live", detail = $"Live, {ago}" }
            : new { state = "stale", detail = $"Silent since {ago}" };
    }
}
