namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Manually queues executions of registered recurring jobs.</summary>
public interface IRecurringJobRunner
{
    /// <summary>Atomically queues a fresh execution using the schedule's stored payload and policy, without changing its next run. Accepts the opaque ID returned by ListRecurring. Returns null when absent.</summary>
    Task<string?> RunRecurring(string scheduleId, CancellationToken cancellationToken = default);
}
