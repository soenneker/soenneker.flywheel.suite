using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Logging.Dtos;
using Soenneker.Flywheel.Core.Responses;

namespace Soenneker.Flywheel.Core.Dashboard.Abstract;

/// <summary>Projects internal storage records into shared dashboard contracts, excluding payloads and lease credentials.</summary>
public interface IDashboardSnapshotFactory
{
    /// <summary>Projects an execution without its payload or lease token.</summary>
    JobView Job(JobRecord job);
    /// <summary>Projects a worker and its public execution snapshots.</summary>
    Communication.Responses.ServerView Server(Responses.ServerView server);
    /// <summary>Projects recurring schedules and pending executions.</summary>
    ScheduleView Schedules(IEnumerable<RecurringJobView> recurring, IEnumerable<JobRecord> scheduled);
    /// <summary>Projects activity buckets.</summary>
    List<Communication.Responses.JobHistoryPoint> History(IEnumerable<Responses.JobHistoryPoint> history);
    /// <summary>Projects retained log entries.</summary>
    List<LogEntry> Logs(IEnumerable<JobLogEntry> logs);
}
