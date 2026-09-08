namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Provides the current number of executions in the running state.</summary>
public interface IJobRunningCountStore
{
    /// <summary>Counts running executions independently of search and history ranges.</summary>
    /// <remarks>This is a current-state gauge, not a cumulative count of attempt starts. A lost lease can remain counted until maintenance recovers it.</remarks>
    /// <returns>The number of jobs still recorded as running.</returns>
    Task<long> GetRunningCount(CancellationToken cancellationToken = default);
}
