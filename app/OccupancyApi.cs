using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace App;

public sealed record SensorTokenRequest(string? Target, string? SetupKey);

public sealed record ReadingRequest(string? Target, int People, double Confidence);

public static class OccupancyApi
{
    public const long MaxPhotoBytes = 2 * 1024 * 1024;

    // Below this the photo tells us too little to change what the map shows.
    private const double MinimumSnapshotConfidence = 0.3;

    private const string SetupPolicy = "sensor-setup";
    private const string SensorPolicy = "sensor";
    private const string SnapshotPolicy = "snapshot";

    /// <summary>
    /// Each caller gets its own window, so nobody can use up another caller's
    /// limit. These run before token checks. <see cref="SnapshotBudget"/> is the
    /// separate cap that protects the OpenAI credits.
    /// </summary>
    public static void AddOccupancyRateLimits(this IServiceCollection services)
    {
        services.AddSingleton<SnapshotBudget>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            AddWindow(options, SetupPolicy, permitsPerMinute: 10, ClientKey);
            AddWindow(options, SensorPolicy, permitsPerMinute: 30, SensorKey);
            // Attendees on venue Wi-Fi can share one address, so leave some room.
            AddWindow(options, SnapshotPolicy, permitsPerMinute: 5, ClientKey);
            options.AddOrganizerRateLimits();
        });
    }

    private static void AddWindow(
        RateLimiterOptions options,
        string name,
        int permitsPerMinute,
        Func<HttpContext, string> partitionKey) =>
        options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // A phone's own token, so requests without it can't reach that phone's limit.
    private static string SensorKey(HttpContext context) =>
        BearerToken(context) ?? ClientKey(context);

    // Behind the platform's proxy every connection has the same address, so use
    // the forwarded header. Only the last entry counts: the proxy appends it,
    // while a caller can put anything in the entries before it.
    internal static string ClientKey(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[^1].Trim();
        return forwarded.Length > 0 ? forwarded : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static void MapOccupancyApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sensor/token", CreateSensorToken).RequireRateLimiting(SetupPolicy);
        app.MapPost("/api/readings", AddReading).RequireRateLimiting(SensorPolicy);
        app.MapPost("/api/snapshots", AddSnapshot)
            .DisableAntiforgery()
            .RequireRateLimiting(SnapshotPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaxPhotoBytes + (64 * 1024)));
    }

    private static async Task<IResult> CreateSensorToken(
        SensorTokenRequest request,
        SensorAuth auth,
        VenueService venues,
        CancellationToken cancellationToken)
    {
        if (!auth.Enabled)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "Sensor reporting isn't set up yet.");
        }

        if (!auth.IsSetupKey(request.SetupKey))
        {
            return Error(StatusCodes.Status401Unauthorized, "That setup key isn't right.");
        }

        var venue = await venues.GetAsync(cancellationToken);
        return venue.IsKnownTarget(request.Target)
            ? Results.Json(new { token = auth.TokenFor(request.Target!) })
            : Error(StatusCodes.Status400BadRequest, "Unknown room or line.");
    }

    private static async Task<IResult> AddReading(
        ReadingRequest request,
        HttpContext context,
        SensorAuth auth,
        VenueService venues,
        IReadingStore store,
        TimeProvider time,
        ILogger<IReadingStore> logger,
        CancellationToken cancellationToken)
    {
        if (!auth.Enabled)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "Sensor reporting isn't set up yet.");
        }

        var venue = await venues.GetAsync(cancellationToken);
        if (!venue.IsKnownTarget(request.Target))
        {
            return Error(StatusCodes.Status400BadRequest, "Unknown room or line.");
        }

        var target = request.Target!;
        if (!auth.IsValidToken(target, BearerToken(context)))
        {
            return Error(StatusCodes.Status401Unauthorized, "This device isn't set up for that room or line.");
        }

        if (request.People < 0 || request.People > venue.MaxPeople(target))
        {
            return Error(StatusCodes.Status400BadRequest, "That count is out of range.");
        }

        var reading = new Reading(target, ReadingSource.Sensor, request.People, Math.Clamp(request.Confidence, 0, 1), time.GetUtcNow());
        return await SaveAsync(store, reading, logger, cancellationToken) ? Results.NoContent() : StoreUnavailable();
    }

    private static async Task<IResult> AddSnapshot(
        [FromForm] string? room,
        IFormFile? photo,
        [FromServices] ISnapshotAnalyzer? analyzer,
        SnapshotBudget budget,
        VenueService venues,
        IReadingStore store,
        TimeProvider time,
        ILogger<IReadingStore> logger,
        CancellationToken cancellationToken)
    {
        if (analyzer is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "Photo reports aren't available yet.");
        }

        var venue = await venues.GetAsync(cancellationToken);
        if (venue.FindRoom(room) is not { } definition)
        {
            return Error(StatusCodes.Status400BadRequest, "Pick a room first.");
        }

        if (photo is null || photo.Length == 0)
        {
            return Error(StatusCodes.Status400BadRequest, "Add a photo first.");
        }

        if (photo.Length > MaxPhotoBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "That photo is too large.");
        }

        var jpeg = await ReadAsync(photo, cancellationToken);
        if (!LooksLikeJpeg(jpeg))
        {
            return Error(StatusCodes.Status415UnsupportedMediaType, "The photo must be a JPEG.");
        }

        // Taken last, so requests that would never reach OpenAI don't spend it.
        if (!budget.TryAcquire())
        {
            return Error(StatusCodes.Status429TooManyRequests, "Lots of photos right now. Please try again in a minute.");
        }

        SnapshotEstimate estimate;
        try
        {
            estimate = await analyzer.AnalyzeAsync(jpeg, definition, cancellationToken);
        }
        catch (SnapshotTimedOutException)
        {
            AppTelemetry.Snapshots.Add(1, new KeyValuePair<string, object?>("outcome", "timed_out"));
            return Error(StatusCodes.Status504GatewayTimeout, "That took too long. Please try again.");
        }
        catch (SnapshotFailedException)
        {
            AppTelemetry.Snapshots.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            return Error(StatusCodes.Status502BadGateway, "We couldn't read that photo. Please try again.");
        }

        var counted = estimate.Confidence >= MinimumSnapshotConfidence;
        AppTelemetry.Snapshots.Add(1, new KeyValuePair<string, object?>("outcome", counted ? "counted" : "low_confidence"));

        if (counted)
        {
            // Fullness, not the head count: a photo rarely shows the whole room.
            var people = (int)Math.Round(definition.Capacity * estimate.FullnessPercent / 100d);
            var reading = new Reading(definition.Id, ReadingSource.Snapshot, people, estimate.Confidence, time.GetUtcNow());
            if (!await SaveAsync(store, reading, logger, cancellationToken))
            {
                return StoreUnavailable();
            }
        }

        return Results.Json(new
        {
            room = definition.Name,
            peopleVisible = estimate.PeopleVisible,
            fullnessPercent = estimate.FullnessPercent,
            openSeats = estimate.OpenSeats,
            confidence = estimate.Confidence,
            counted,
        });
    }

    private static async Task<bool> SaveAsync(
        IReadingStore store,
        Reading reading,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.AddAsync(reading, cancellationToken);
        }
        catch (Exception e) when (e is ReadingStoreException or HttpRequestException or Elastic.Transport.TransportException)
        {
            logger.LogError(e, "Couldn't save a {Source} reading", reading.Source);
            return false;
        }

        AppTelemetry.Readings.Add(1, new KeyValuePair<string, object?>("source", reading.Source.ToString().ToLowerInvariant()));
        return true;
    }

    private static async Task<byte[]> ReadAsync(IFormFile photo, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream((int)photo.Length);
        await photo.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static bool LooksLikeJpeg(byte[] bytes) =>
        bytes is [0xFF, 0xD8, 0xFF, ..];

    internal static string? BearerToken(HttpContext context)
    {
        const string prefix = "Bearer ";
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..] : null;
    }

    private static IResult StoreUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, "Readings can't be saved right now.");

    private static IResult Error(int status, string message) =>
        Results.Json(new { error = message }, statusCode: status);
}
