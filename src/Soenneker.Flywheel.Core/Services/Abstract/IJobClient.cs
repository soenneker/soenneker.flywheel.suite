using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Typed producer API. A completed call means persisted, not executed. Handlers must be idempotent. For trimmed applications, configure Flywheel with a generated JsonSerializerContext for all job payloads.</summary>
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

    /// <summary>Serializes and persists a registered job; use a stable key for ambiguous enqueue retries.
    /// Set JobPolicy.PartitionKey for fair tenant dispatch and configure MethodPolicy.MaxConcurrencyPerPartition
    /// through ConfigureMethod to limit concurrent executions per tenant. Payloads survive retries and lease recovery.</summary>
    Task<string> Enqueue<T>(JobDefinition<T> job, T payload, JobPolicy? policy = null, TimeSpan? delay = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default);
    /// <summary>Persists a job for execution at or after the given time; past times enqueue immediately.</summary>
    Task<string> Schedule<T>(JobDefinition<T> job, T payload, DateTimeOffset scheduledAt, JobPolicy? policy = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default);
    /// <summary>Changes queue position or releases a pending execution's delay. Returns false when the execution is no longer pending.
    /// Pool and execution limits still apply; waiting chain steps cannot be released.</summary>
    Task<bool> UpdateQueue(string id, Communication.Enums.JobQueueAction action, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This client does not support queue actions.");
    /// <summary>Updates pool assignments and distributed limits for a registered method at runtime. Requires a provider supporting method policies.</summary>
    Task ConfigureMethod<T>(JobDefinition<T> job, MethodPolicy policy, CancellationToken cancellationToken = default);
    /// <summary>Registers a reusable manual job without scheduling an execution. Existing definitions remain unchanged.</summary>
    Task<bool> Manual<T>(string id, JobDefinition<T> job, T payload, JobPolicy? policy = null, CancellationToken cancellationToken = default);
    /// <summary>Queues a fresh execution of a manual definition, or returns null if it does not exist.</summary>
    Task<string?> RunManual(string id, CancellationToken cancellationToken = default);

    /// <summary>Registers a fixed-interval recurring job, leaving an existing schedule unchanged.</summary>
    Task<bool> Recurring<T>(string id, JobDefinition<T> job, T payload, TimeSpan interval, JobPolicy? policy = null,
        CancellationToken cancellationToken = default);

    /// <summary>Registers a cron schedule in the specified time zone (UTC by default). Existing schedule IDs are unchanged.
    /// Use five fields, or six with includeSeconds. Missed occurrences coalesce; separate occurrences may overlap.
    /// </summary>
    Task<bool> Schedule<T>(string id, JobDefinition<T> job, T payload, string expression, string timeZoneId = "UTC",
        JobPolicy? policy = null, bool includeSeconds = false, CancellationToken cancellationToken = default);

    /// <summary>Atomically saves 1–100 typed steps and returns IDs in order. Each step waits for predecessor success.
    /// Retries keep successors waiting; final failure or cancellation cancels them. Reuse a chain key to retry submission safely.</summary>
    Task<IReadOnlyList<string>> Chain(IReadOnlyList<JobStep> steps, string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>Attaches a typed continuation to an existing retained job. Delay begins when its condition is met.
    /// Final failure/cancellation are distinct from retrying attempts; unmet conditions skip the dependent branch.</summary>
    Task<string> ContinueWith<T>(string parentId, JobDefinition<T> job, T payload,
        Communication.Enums.ContinuationCondition condition = Communication.Enums.ContinuationCondition.OnSuccess,
        JobPolicy? policy = null, TimeSpan? delay = null, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This client does not support continuations.");
    /// <summary>Atomically submits 1–999 parallel jobs and an optional final step that waits for all members.
    /// Completion supports OnSuccess or OnCompleted. Returns all execution IDs, including the final step.
    /// Reuse a stable idempotency key when retrying submission. Each workflow supports at most 1000 total jobs.</summary>
    Task<Communication.Responses.WorkflowSubmission> Batch(IReadOnlyList<JobStep> jobs, JobStep? continuation = null,
        Communication.Enums.ContinuationCondition condition = Communication.Enums.ContinuationCondition.OnSuccess,
        string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This client does not support batches.");
    /// <summary>Replaces an existing interval definition. Recalculates its next run; existing executions and history are unchanged.
    /// Returns false if missing or the optional editor revision changed. Pass enabled=false to keep it paused.</summary>
    Task<bool> UpdateRecurring<T>(string id, JobDefinition<T> job, T payload, TimeSpan interval, JobPolicy? policy = null,
        bool enabled = true, long? expectedRevision = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This client does not support definition updates.");
    /// <summary>Replaces an existing cron definition and recalculates its next run. Returns false if missing or the optional revision changed.</summary>
    Task<bool> UpdateSchedule<T>(string id, JobDefinition<T> job, T payload, string expression, string timeZoneId = "UTC",
        JobPolicy? policy = null, bool includeSeconds = false, bool enabled = true, long? expectedRevision = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("This client does not support definition updates.");
    /// <summary>Pauses or resumes future recurring occurrences without cancelling active work. Resume does not catch up missed runs.</summary>
    Task<bool> SetRecurringEnabled(string id, bool enabled, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This client does not support definition updates.");
    /// <summary>Deletes a recurring definition while preserving all existing executions.</summary>
    Task<bool> DeleteRecurring(string id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This client does not support definition updates.");
    /// <summary>Replaces an existing manual definition's payload and policy. Returns false if missing.</summary>
    Task<bool> UpdateManual<T>(string id, JobDefinition<T> job, T payload, JobPolicy? policy = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("This client does not support definition updates.");
    /// <summary>Deletes a manual definition while preserving all existing executions.</summary>
    Task<bool> DeleteManual(string id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This client does not support definition updates.");
}
