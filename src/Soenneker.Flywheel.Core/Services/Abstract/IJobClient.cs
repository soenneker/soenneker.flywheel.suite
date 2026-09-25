using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Typed producer API. A completed call means persisted, not executed. Handlers must be idempotent.</summary>
/// <remarks>Register a source-generated JsonSerializerContext containing each job payload type. Payload serialization never discovers contracts through reflection.</remarks>
public interface IJobClient
{
    /// <summary>Schedules the latest payload for a debounce ID after the delay. Each submission cancels the previous execution
    /// and resets the due time using the store clock. Running executions are cancelled cooperatively.</summary>
    Task EnqueueDebounced<T>(JobDefinition<T> job, T payload, string debounceId, TimeSpan delay,
        JobPolicy? policy = null, CancellationToken cancellationToken = default);

    /// <summary>Queues the newest request for a key after a quiet period, cancelling its predecessor.
    /// Reuse requestId and requestedAt when retrying an ambiguous submission. The job store must support IJobDebounceCoordinator.</summary>
    Task EnqueueDebounced<T>(JobDefinition<T> job, T payload, string key, string requestId, DateTimeOffset requestedAt,
        TimeSpan delay, JobPolicy? policy = null, CancellationToken cancellationToken = default);

    /// <summary>Reads the latest accepted request identifier for a debounce key. Requires IJobDebounceCoordinator.</summary>
    ValueTask<string?> GetDebouncedRequestId(string key, CancellationToken cancellationToken = default);

    /// <summary>Runs an idempotent commit only if the request is current, excluding concurrent replacement submissions.
    /// A null requestId matches a key with no accepted request. Requires IJobDebounceCoordinator.</summary>
    ValueTask<bool> CommitDebounced(string key, string? requestId, Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default);

    /// <summary>Submits a registered job once for the calling application instance.
    /// Only that instance can claim it. Repeated calls on the same instance return the
    /// original ID even after retention removes the record. Payload and policy from the first call win.
    /// Retries and lease recovery still apply; this is not an exactly-once execution guarantee.</summary>
    Task<string> EnqueueForCurrentInstance<T>(JobDefinition<T> job, T payload, JobPolicy? policy = null,
        CancellationToken cancellationToken = default);

    /// <summary>Serializes and persists a registered job; use a stable key for ambiguous enqueue retries.</summary>
    Task<string> Enqueue<T>(JobDefinition<T> job, T payload, JobPolicy? policy = null, TimeSpan? delay = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default);
    /// <summary>Persists a job for execution at or after the given time; past times enqueue immediately.</summary>
    Task<string> Schedule<T>(JobDefinition<T> job, T payload, DateTimeOffset scheduledAt, JobPolicy? policy = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default);
    /// <summary>Updates distributed limits for a registered method at runtime. Requires a provider supporting method policies.</summary>
    Task ConfigureMethod<T>(JobDefinition<T> job, MethodPolicy policy, CancellationToken cancellationToken = default);
    /// <summary>Registers a fixed-interval recurring job, leaving an existing schedule unchanged.</summary>
    Task<bool> Recurring<T>(string id, JobDefinition<T> job, T payload, TimeSpan interval, JobPolicy? policy = null,
        CancellationToken cancellationToken = default);

    /// <summary>Registers a cron schedule in the specified time zone (UTC by default). Existing schedule IDs are unchanged.
    /// Use five fields, or six with includeSeconds. Missed occurrences coalesce; separate occurrences may overlap.</summary>
    Task<bool> Schedule<T>(string id, JobDefinition<T> job, T payload, string expression, string timeZoneId = "UTC",
        JobPolicy? policy = null, bool includeSeconds = false, CancellationToken cancellationToken = default);

    /// <summary>Atomically saves 1–100 typed steps and returns IDs in order. Each step waits for predecessor success.
    /// Retries keep successors waiting; final failure or cancellation cancels them. Reuse a chain key to retry submission safely.</summary>
    Task<IReadOnlyList<string>> Chain(IReadOnlyList<JobStep> steps, string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}
