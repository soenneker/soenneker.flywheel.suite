using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>A durable snapshot. Millisecond timestamps are UTC Unix time from storage.</summary>
public sealed record JobRecord
{
    /// <summary>Returns Queued for eligible pending work, or the durable state name otherwise.</summary>
    public string DisplayState(long now) => State == Enums.JobState.Scheduled && DueAt <= now ? "Queued" : State.Name;

    /// <summary>Unique identifier of the job or retained entry.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    /// <summary>Recurring schedule that created this execution, or null for non-recurring jobs.</summary>
    [JsonPropertyName("scheduleId")]
    public string? ScheduleId { get; init; }
    /// <summary>Stable job name used for registration and persisted work.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    /// <summary>Exact application build allowed to execute this job, or null for unrestricted work.</summary>
    [JsonPropertyName("applicationVersion")]
    public string? ApplicationVersion { get; init; }
    /// <summary>Optional human-readable description displayed by dashboard clients.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
    /// <summary>JSON-serialized payload passed to the job handler.</summary>
    [JsonPropertyName("payload")]
    public required string Payload { get; init; }
    /// <summary>Execution timeout, attempt limit, and retry settings.</summary>
    [JsonPropertyName("policy")]
    public required JobPolicy Policy { get; init; }
    /// <summary>Current durable lifecycle state of the job.</summary>
    [JsonPropertyName("state")]
    public Enums.JobState State { get; init; }
    /// <summary>Execution attempt number.</summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }
    /// <summary>Fencing version identifying the current lease generation.</summary>
    [JsonPropertyName("version")]
    public long Version { get; init; }
    /// <summary>Creation time in UTC Unix milliseconds.</summary>
    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; init; }
    /// <summary>Last update time in UTC Unix milliseconds.</summary>
    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; init; }
    /// <summary>Start of the latest attempt in UTC Unix milliseconds, or zero when unavailable.</summary>
    [JsonPropertyName("startedAt")]
    public long StartedAt { get; init; }
    /// <summary>Terminal completion time in UTC Unix milliseconds, or zero when unfinished or unavailable.</summary>
    [JsonPropertyName("completedAt")]
    public long CompletedAt { get; init; }
    /// <summary>Next eligible execution time in UTC Unix milliseconds.</summary>
    [JsonPropertyName("dueAt")]
    public long DueAt { get; init; }
    /// <summary>Lease expiration time in UTC Unix milliseconds.</summary>
    [JsonPropertyName("leaseUntil")]
    public long LeaseUntil { get; init; }
    /// <summary>Identifier of the node owning the lease, or null when unowned.</summary>
    [JsonPropertyName("owner")]
    public string? Owner { get; init; }
    /// <summary>Current lease ownership token, or null when unleased.</summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }
    /// <summary>Whether cancellation has been requested for this job.</summary>
    [JsonPropertyName("cancelRequested")]
    public bool CancelRequested { get; init; }
    /// <summary>Retained failure details, or null when no error is recorded.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
    /// <summary>Predecessor whose success releases this chain step, or null for a root job.</summary>
    [JsonPropertyName("parentJobId")]
    public string? ParentJobId { get; init; }
    /// <summary>Next step in the saved chain, or null for the last step.</summary>
    [JsonPropertyName("nextJobId")]
    public string? NextJobId { get; init; }
    /// <summary>Delay in milliseconds after predecessor success before this step can run.</summary>
    [JsonPropertyName("delayAfterParent")]
    public long DelayAfterParent { get; init; }
    /// <summary>Latest progress percentage for the current attempt, or null when the handler has not reported progress.</summary>
    [JsonPropertyName("progress")]
    public double? Progress { get; init; }
    /// <summary>Optional status text accompanying the latest progress report.</summary>
    [JsonPropertyName("progressMessage")]
    public string? ProgressMessage { get; init; }
    /// <summary>Time of the latest progress report in UTC Unix milliseconds, or zero when none has been reported.</summary>
    [JsonPropertyName("progressUpdatedAt")]
    public long ProgressUpdatedAt { get; init; }
}
