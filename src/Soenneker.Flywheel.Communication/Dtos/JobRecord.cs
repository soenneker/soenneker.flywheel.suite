namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>A durable snapshot. Millisecond timestamps are UTC Unix time from storage.</summary>
public sealed record JobRecord
{
    /// <summary>Returns Queued for eligible pending work, or the durable state name otherwise.</summary>
    public string DisplayState(long now) => State == Enums.JobState.Scheduled && DueAt <= now ? "Queued" : State.Name;

    /// <summary>Unique identifier of the job or retained entry.</summary>
    public required string Id { get; init; }
    /// <summary>Stable job name used for registration and persisted work.</summary>
    public required string Name { get; init; }
    /// <summary>Optional human-readable description displayed by dashboard clients.</summary>
    public string? Description { get; init; }
    /// <summary>JSON-serialized payload passed to the job handler.</summary>
    public required string Payload { get; init; }
    /// <summary>Execution timeout, attempt limit, and retry settings.</summary>
    public required JobPolicy Policy { get; init; }
    /// <summary>Current durable lifecycle state of the job.</summary>
    public Enums.JobState State { get; init; }
    /// <summary>Execution attempt number.</summary>
    public int Attempt { get; init; }
    /// <summary>Fencing version identifying the current lease generation.</summary>
    public long Version { get; init; }
    /// <summary>Creation time in UTC Unix milliseconds.</summary>
    public long CreatedAt { get; init; }
    /// <summary>Last update time in UTC Unix milliseconds.</summary>
    public long UpdatedAt { get; init; }
    /// <summary>Start of the latest attempt in UTC Unix milliseconds, or zero when unavailable.</summary>
    public long StartedAt { get; init; }
    /// <summary>Terminal completion time in UTC Unix milliseconds, or zero when unfinished or unavailable.</summary>
    public long CompletedAt { get; init; }
    /// <summary>Next eligible execution time in UTC Unix milliseconds.</summary>
    public long DueAt { get; init; }
    /// <summary>Lease expiration time in UTC Unix milliseconds.</summary>
    public long LeaseUntil { get; init; }
    /// <summary>Identifier of the node owning the lease, or null when unowned.</summary>
    public string? Owner { get; init; }
    /// <summary>Current lease ownership token, or null when unleased.</summary>
    public string? Token { get; init; }
    /// <summary>Whether cancellation has been requested for this job.</summary>
    public bool CancelRequested { get; init; }
    /// <summary>Retained failure details, or null when no error is recorded.</summary>
    public string? Error { get; init; }
    /// <summary>Predecessor whose success releases this chain step, or null for a root job.</summary>
    public string? ParentJobId { get; init; }
    /// <summary>Next step in the saved chain, or null for the last step.</summary>
    public string? NextJobId { get; init; }
    /// <summary>Delay in milliseconds after predecessor success before this step can run.</summary>
    public long DelayAfterParent { get; init; }
    /// <summary>Latest progress percentage for the current attempt, or null when the handler has not reported progress.</summary>
    public double? Progress { get; init; }
    /// <summary>Optional status text accompanying the latest progress report.</summary>
    public string? ProgressMessage { get; init; }
    /// <summary>Time of the latest progress report in UTC Unix milliseconds, or zero when none has been reported.</summary>
    public long ProgressUpdatedAt { get; init; }
}
