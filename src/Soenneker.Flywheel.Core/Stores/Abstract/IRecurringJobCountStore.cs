namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Optional dashboard read for the total number of active recurring schedules.</summary>
public interface IRecurringJobCountStore
{
    /// <summary>Returns the number of recurring schedules with a next due time, without a page limit.</summary>
    Task<long> GetRecurringCount(CancellationToken cancellationToken = default);
}
