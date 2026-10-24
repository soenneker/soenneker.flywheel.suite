using System.Threading.RateLimiting;

namespace Soenneker.Flywheel.Core.Dashboard;

/// <summary>Bounds concurrent password verification without consuming a shared time-window allowance.</summary>
public sealed class DashboardLoginLimiter : IDisposable
{
    private readonly ConcurrencyLimiter _limiter = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 2,
        QueueLimit = 0
    });

    /// <summary>Attempts to reserve a password verification slot. Dispose the lease after verification.</summary>
    public RateLimitLease Acquire() => _limiter.AttemptAcquire();

    public void Dispose() => _limiter.Dispose();
}
