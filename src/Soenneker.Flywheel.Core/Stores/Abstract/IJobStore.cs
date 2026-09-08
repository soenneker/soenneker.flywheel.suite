using Soenneker.Flywheel.Core.Responses;
using Soenneker.Flywheel.Core.Requests;
using Soenneker.Flywheel.Core.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Authoritative persistence and atomic lifecycle operations. Implementations must be safe across processes.</summary>
public interface IJobStore
{
    /// <summary>Atomically persists a job and its eligibility index; a retained idempotency key returns the original id.</summary>
    Task<string> Enqueue(EnqueueRequest request, CancellationToken cancellationToken = default);

    /// <summary>Atomically claims one due job, assigning a unique capability and increasing its fencing version.</summary>
    Task<JobLease?> Claim(string owner, TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>Renews only an unexpired current lease using storage time.</summary>
    Task<Enums.LeaseStatus> Renew(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>Commits an outcome or retry atomically. Returns false for stale/expired ownership.</summary>
    Task<bool> Finish(JobLease lease, Enums.JobOutcome outcome, string? error, TimeSpan retryDelay,
        CancellationToken cancellationToken = default);

    /// <summary>Durably cancels pending work or requests cooperative cancellation of an execution.</summary>
    Task<bool> Cancel(string id, CancellationToken cancellationToken = default);

    /// <summary>Recovers a bounded batch of expired leases and materializes due recurring occurrences atomically.</summary>
    Task Maintain(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>Reads authoritative state.</summary>
    Task<JobRecord?> Get(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists a bounded page of newest jobs, including terminal states.</summary>
    Task<IReadOnlyList<JobRecord>> List(int offset = 0, int count = 50, CancellationToken cancellationToken = default);

    /// <summary>Searches all retained jobs by literal, ordinal case-insensitive substring of name, id, state or owner.
    /// Trims the query (maximum 200 characters); empty queries match all jobs. Pages are newest first and are live views.
    /// Payloads and errors are not searched. Offset applies to matches, not the underlying job list.</summary>
    Task<JobSearchResult> Search(string? query, int offset = 0, int count = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a fixed-interval UTC schedule if absent; missed ticks coalesce into one occurrence.</summary>
    Task<bool> AddRecurring(string id, EnqueueRequest request, TimeSpan interval,
        CancellationToken cancellationToken = default);
}