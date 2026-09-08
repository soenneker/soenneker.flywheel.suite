using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Typed producer API. A completed call means persisted, not executed. Handlers must be idempotent.</summary>
public interface IJobClient
{
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
