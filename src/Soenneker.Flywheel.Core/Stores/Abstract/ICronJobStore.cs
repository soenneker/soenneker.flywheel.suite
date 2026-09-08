using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Persists cron schedules and atomically materializes their occurrences using storage time.</summary>
public interface ICronJobStore
{
    /// <summary>Creates a schedule if absent. Existing IDs are unchanged. Missed occurrences coalesce into one job.</summary>
    Task<bool> AddCron(string id, EnqueueRequest request, string expression, string timeZoneId = "UTC",
        bool includeSeconds = false, CancellationToken cancellationToken = default);
}
