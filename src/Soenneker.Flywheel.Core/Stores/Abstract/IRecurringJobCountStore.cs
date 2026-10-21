namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Optional dashboard read for the total number of saved recurring schedules, including paused definitions.</summary>
public interface IRecurringJobCountStore
{
    /// <summary>Returns the number of saved recurring schedules, including paused definitions, without a page limit.</summary>
    Task<long> GetRecurringCount(CancellationToken cancellationToken = default);
}
