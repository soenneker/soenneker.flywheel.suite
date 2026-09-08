using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Responses;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Optional dashboard reads for registered schedules and pending executions.</summary>
public interface IJobScheduleStore
{
    /// <summary>Lists up to count recurring schedules ordered by next due time. Count must be between 1 and 200.</summary>
    Task<IReadOnlyList<RecurringJobView>> ListRecurring(int count = 50, CancellationToken cancellationToken = default);

    /// <summary>Lists up to count pending executions ordered by due time, including retries and recurring occurrences. Count must be between 1 and 200.</summary>
    Task<IReadOnlyList<JobRecord>> ListScheduled(int count = 50, CancellationToken cancellationToken = default);
}
