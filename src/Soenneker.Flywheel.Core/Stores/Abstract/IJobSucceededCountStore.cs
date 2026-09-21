namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Provides the total number of retained succeeded jobs.</summary>
public interface IJobSucceededCountStore
{
    /// <summary>Counts retained succeeded jobs independently of search, pagination, and history ranges.</summary>
    Task<long> GetSucceededCount(CancellationToken cancellationToken = default);
}
