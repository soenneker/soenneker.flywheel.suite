namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Manually queues executions of registered recurring jobs.</summary>
public interface IRecurringJobRunner
{
    /// <summary>Atomically queues an execution using the schedule's stored payload and policy, without changing its next run.
    /// When coalescing is enabled, returns the existing unfinished execution instead. Accepts the opaque ID returned by ListRecurring. Returns null when absent.</summary>
    Task<string?> RunRecurring(string scheduleId, CancellationToken cancellationToken = default);
}
