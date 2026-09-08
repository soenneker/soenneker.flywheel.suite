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
/// <param name="MaxAttempts">Maximum attempts, or null when an older server does not provide it.</param>
/// <param name="TimeoutSeconds">Execution timeout per attempt, in seconds.</param>
/// <param name="Priority">Dispatch priority for eligible work.</param>
/// <param name="Progress">Latest progress percentage for the current attempt.</param>
/// <param name="ProgressMessage">Optional status text accompanying the latest progress report.</param>
/// <param name="ProgressUpdatedAt">Time of the latest progress report in UTC Unix milliseconds.</param>
/// <param name="Description">Optional human-readable description of the job.</param>
public sealed record JobView(string Id, string Name, string State, int Attempt, long UpdatedAt, bool CancelRequested, string? Error,
    long Version, long CreatedAt, long DueAt, long LeaseUntil, string? Owner, string? ParentJobId = null, string? NextJobId = null,
    int? MaxAttempts = null, double? TimeoutSeconds = null, string? Priority = null, double? Progress = null,
    string? ProgressMessage = null, long ProgressUpdatedAt = 0, string? Description = null);
