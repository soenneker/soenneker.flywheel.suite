using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <param name="Id">Unique identifier of the job or retained entry.</param>
/// <param name="Name">Stable job name used for registration and persisted work.</param>
/// <param name="State">Current durable lifecycle state of the job.</param>
/// <param name="Attempt">Execution attempt number.</param>
/// <param name="UpdatedAt">Last update time in UTC Unix milliseconds.</param>
/// <param name="CancelRequested">Whether cancellation has been requested for this job.</param>
/// <param name="Error">Retained failure details, or null when no error is recorded.</param>
/// <param name="Version">Fencing version of the current execution lease.</param>
/// <param name="CreatedAt">Creation time in UTC Unix milliseconds.</param>
/// <param name="DueAt">Next eligible execution time in UTC Unix milliseconds.</param>
/// <param name="LeaseUntil">Lease expiration time in UTC Unix milliseconds.</param>
/// <param name="Owner">Node that owns the current lease, if any.</param>
/// <param name="ParentJobId">Previous step in a saved chain, if any.</param>
/// <param name="NextJobId">Next step in a saved chain, if any.</param>
/// <param name="MaxAttempts">Maximum execution attempts.</param>
/// <param name="TimeoutSeconds">Execution timeout per attempt, in seconds.</param>
/// <param name="Priority">Dispatch priority for eligible work.</param>
/// <param name="Progress">Latest progress percentage for the current attempt.</param>
/// <param name="ProgressMessage">Optional status text accompanying the latest progress report.</param>
/// <param name="ProgressUpdatedAt">Time of the latest progress report in UTC Unix milliseconds.</param>
/// <param name="Description">Optional human-readable description of the job.</param>
/// <param name="StartedAt">Start of the latest attempt in UTC Unix milliseconds, or zero when unavailable.</param>
/// <param name="CompletedAt">Terminal completion time in UTC Unix milliseconds, or zero when unavailable.</param>
public sealed record JobView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    [property: JsonPropertyName("cancelRequested")] bool CancelRequested,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("dueAt")] long DueAt,
    [property: JsonPropertyName("leaseUntil")] long LeaseUntil,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("parentJobId")] string? ParentJobId = null,
    [property: JsonPropertyName("nextJobId")] string? NextJobId = null,
    [property: JsonPropertyName("maxAttempts")] int? MaxAttempts = null,
    [property: JsonPropertyName("timeoutSeconds")] double? TimeoutSeconds = null,
    [property: JsonPropertyName("priority")] string? Priority = null,
    [property: JsonPropertyName("progress")] double? Progress = null,
    [property: JsonPropertyName("progressMessage")] string? ProgressMessage = null,
    [property: JsonPropertyName("progressUpdatedAt")] long ProgressUpdatedAt = 0,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("startedAt")] long StartedAt = 0,
    [property: JsonPropertyName("completedAt")] long CompletedAt = 0);
