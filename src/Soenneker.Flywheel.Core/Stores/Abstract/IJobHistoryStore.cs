using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Reads durable job activity independently of dashboard sessions.</summary>
public interface IJobHistoryStore
{
    /// <summary>Gets how long aggregate job activity is retained.</summary>
    TimeSpan HistoryRetention { get; }

    /// <summary>Returns transitions in five-minute UTC buckets for the requested range.</summary>
    Task<IReadOnlyList<JobHistoryPoint>> GetHistory(DateTimeOffset startAt, DateTimeOffset endAt,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent day of retained transitions.</summary>
    Task<IReadOnlyList<JobHistoryPoint>> GetHistory(CancellationToken cancellationToken = default)
    {
        DateTimeOffset endAt = DateTimeOffset.UtcNow;
        DateTimeOffset startAt = endAt - (HistoryRetention < TimeSpan.FromDays(1) ? HistoryRetention : TimeSpan.FromDays(1));
        return GetHistory(startAt, endAt, cancellationToken);
    }
}
