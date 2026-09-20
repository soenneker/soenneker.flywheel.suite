namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Provides the total number of retained failed jobs.</summary>
public interface IJobFailedCountStore
{
    /// <summary>Counts retained dead-lettered jobs independently of search, pagination, and history ranges.</summary>
    Task<long> GetFailedCount(CancellationToken cancellationToken = default);
}
