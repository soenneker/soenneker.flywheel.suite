using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Logging.Dtos;

namespace Soenneker.Flywheel.Core.Dashboard;

public sealed class DashboardSnapshotFactory : IDashboardSnapshotFactory
{
    public JobView Job(JobRecord job) => new(job.Id, job.Name, job.DisplayState(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), job.Attempt, job.UpdatedAt, job.CancelRequested,
        job.Error, job.Version, job.CreatedAt, job.DueAt, job.LeaseUntil, job.Owner, job.ParentJobId, job.NextJobId,
        job.Policy.MaxAttempts, job.Policy.Timeout.TotalSeconds, job.Policy.Priority.Name, job.Progress, job.ProgressMessage,
        job.ProgressUpdatedAt, job.Description, job.StartedAt, job.CompletedAt);

    public Communication.Responses.ServerView Server(Communication.Responses.WorkerServerView server) =>
        new(server.Id, server.ExpiresAt, server.Workers, server.RunningJobs.Select(Job).ToList());

    public ScheduleView Schedules(IEnumerable<RecurringJobView> recurring, IEnumerable<JobRecord> scheduled) =>
        new(recurring.Select(s => new RecurringScheduleView(s.Id, s.Name, s.Interval, s.DueAt, s.Cron, s.TimeZoneId, s.IncludeSeconds, s.LastExecutionStatus)).ToList(),
            scheduled.Select(Job).ToList());

    public List<Communication.Responses.JobHistoryPoint> History(IEnumerable<Communication.Responses.JobHistoryPoint> history) =>
        history.ToList();

    public List<LogEntry> Logs(IEnumerable<JobLogEntry> logs) =>
        logs.Select(l => new LogEntry(l.Id, l.Timestamp, l.Attempt, l.Level, l.Category, l.Message)).ToList();
}
