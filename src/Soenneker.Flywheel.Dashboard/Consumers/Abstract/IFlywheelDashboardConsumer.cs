using Soenneker.Dtos.Results.Operation;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Consumers.Abstract;

/// <summary>Typed dashboard operations. Results preserve status codes for sign-in, missing resources, and unsupported store capabilities.</summary>
public interface IFlywheelDashboardConsumer
{
    /// <summary>Searches executions, optionally within an activity range.</summary>
    ValueTask<OperationResult<SearchResult>> Search(string query = "", int offset = 0, int count = 50, DateTimeOffset? startAt = null, DateTimeOffset? endAt = null, CancellationToken cancellationToken = default);
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
    /// <summary>Requests cancellation of an execution.</summary>
    ValueTask<OperationResult<object>> CancelJob(string id, CancellationToken cancellationToken = default);
    /// <summary>Starts an execution from a recurring schedule.</summary>
    ValueTask<OperationResult<StartedJob>> RunRecurring(string id, CancellationToken cancellationToken = default);
}
