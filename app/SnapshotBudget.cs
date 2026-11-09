using System.Threading.RateLimiting;

namespace App;

/// <summary>
/// A hard limit on OpenAI calls from this pod, whoever is asking. It caps what
/// the public snapshot page can spend from the event credits.
/// </summary>
public sealed class SnapshotBudget : IDisposable
{
    public const int CallsPerMinute = 10;

    private readonly FixedWindowRateLimiter _limiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = CallsPerMinute,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    public bool TryAcquire()
    {
        using var lease = _limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
