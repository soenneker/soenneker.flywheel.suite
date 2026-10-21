using Soenneker.Dtos.Results.Operation;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Consumers.Abstract;

/// <summary>Typed dashboard operations. Results preserve status codes for sign-in, missing resources, and unsupported store capabilities.</summary>
public interface IFlywheelDashboardConsumer
{
    /// <summary>Reads up to 200 retained logs before a stable entry ID, in chronological order.</summary>
    ValueTask<OperationResult<List<LogEntry>>> GetLogs(string id, string beforeId, CancellationToken cancellationToken = default);

    /// <summary>Lists registered manual jobs and their latest execution status.</summary>
    ValueTask<OperationResult<List<ManualJobView>>> GetManualJobs(CancellationToken cancellationToken = default);
    /// <summary>Queues a fresh execution of a registered manual job.</summary>
    ValueTask<OperationResult<StartedJob>> RunManual(string id, CancellationToken cancellationToken = default);

    /// <summary>Reads the authenticated dashboard identity.</summary>
    ValueTask<OperationResult<DashboardUser>> GetUser(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>Searches executions, optionally within an activity range.</summary>
    ValueTask<OperationResult<SearchResult>> Search(string query = "", int offset = 0, int count = 50, DateTimeOffset? startAt = null, DateTimeOffset? endAt = null, CancellationToken cancellationToken = default, string? excludedStates = null);
    /// <summary>Returns a static distribution of all retained jobs matching the search and optional UTC range.</summary>
    ValueTask<OperationResult<List<JobHistoryPoint>>> GetSearchHistory(string query, DateTimeOffset? startAt = null,
        DateTimeOffset? endAt = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>Reads activity retention options.</summary>
    ValueTask<OperationResult<HistoryOptions>> GetHistoryOptions(CancellationToken cancellationToken = default);
    /// <summary>Reads activity buckets for a UTC range.</summary>
    ValueTask<OperationResult<List<JobHistoryPoint>>> GetHistory(DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken cancellationToken = default);
    /// <summary>Reads recurring schedules and pending executions.</summary>
    ValueTask<OperationResult<ScheduleView>> GetSchedules(CancellationToken cancellationToken = default);
    /// <summary>Reads one execution.</summary>
    ValueTask<OperationResult<JobView>> GetJob(string id, CancellationToken cancellationToken = default);
    /// <summary>Reads live worker servers.</summary>
    ValueTask<OperationResult<List<ServerView>>> GetServers(CancellationToken cancellationToken = default);
    /// <summary>Reads one worker server.</summary>
    ValueTask<OperationResult<ServerView>> GetServer(string id, CancellationToken cancellationToken = default);
    /// <summary>Signs in with dashboard credentials and antiforgery protection.</summary>
    ValueTask<OperationResult<object>> Login(string username, string password, CancellationToken cancellationToken = default);
    /// <summary>Signs out the current dashboard session.</summary>
    ValueTask<OperationResult<object>> Logout(CancellationToken cancellationToken = default);
    /// <summary>Moves a queued execution or makes a scheduled execution eligible now, preserving dispatch restrictions.</summary>
    ValueTask<OperationResult<object>> UpdateQueue(string id, Soenneker.Flywheel.Communication.Enums.JobQueueAction action, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Queue actions are not supported.");
    /// <summary>Requests cancellation of an execution.</summary>
    ValueTask<OperationResult<object>> CancelJob(string id, CancellationToken cancellationToken = default);
    /// <summary>Queues a fresh standalone execution using a finished job's payload and policy.</summary>
    ValueTask<OperationResult<StartedJob>> RunAgain(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Starts an execution from a recurring schedule.</summary>
    ValueTask<OperationResult<StartedJob>> RunRecurring(string id, CancellationToken cancellationToken = default);

    /// <summary>Reads an execution's complete workflow without payloads or lease credentials.</summary>
    ValueTask<OperationResult<WorkflowView>> GetWorkflow(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Reads a recurring definition for editing, including its payload and concurrency revision.</summary>
    ValueTask<OperationResult<ScheduleDefinitionView>> GetScheduleDefinition(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Updates future recurring executions, rejecting stale revisions.</summary>
    ValueTask<OperationResult<object>> UpdateSchedule(string id, Soenneker.Flywheel.Communication.Requests.ScheduleUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Pauses or resumes future recurring occurrences using the displayed revision.</summary>
    ValueTask<OperationResult<object>> SetScheduleEnabled(string id, bool enabled, long revision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Deletes a recurring definition without deleting execution history.</summary>
    ValueTask<OperationResult<object>> DeleteSchedule(string id, long revision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Reads a manual definition for editing, including its payload.</summary>
    ValueTask<OperationResult<Soenneker.Flywheel.Communication.Requests.EnqueueRequest>> GetManualDefinition(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Updates future manual executions.</summary>
    ValueTask<OperationResult<object>> UpdateManual(string id, Soenneker.Flywheel.Communication.Requests.EnqueueRequest update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Deletes a manual definition without deleting execution history.</summary>
    ValueTask<OperationResult<object>> DeleteManual(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
