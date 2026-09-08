using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Logging.Dtos;
using Soenneker.Flywheel.Core.Responses;

namespace Soenneker.Flywheel.Core.Dashboard;

public sealed class DashboardSnapshotFactory : IDashboardSnapshotFactory
{
    public JobView Job(JobRecord job) => new(job.Id, job.Name, job.State.Name, job.Attempt, job.UpdatedAt, job.CancelRequested,
        job.Error, job.Version, job.CreatedAt, job.DueAt, job.LeaseUntil, job.Owner, job.ParentJobId, job.NextJobId,
        job.Policy.MaxAttempts, job.Policy.Timeout.TotalSeconds, job.Policy.Priority.Name, job.Progress, job.ProgressMessage,
        job.ProgressUpdatedAt, job.Description);

    public Communication.Responses.ServerView Server(Responses.ServerView server) =>
        new(server.Id, server.ExpiresAt, server.Workers, server.RunningJobs.Select(Job).ToList());

    public ScheduleView Schedules(IEnumerable<RecurringJobView> recurring, IEnumerable<JobRecord> scheduled) =>
        new(recurring.Select(s => new RecurringScheduleView(s.Id, s.Name, s.Interval, s.DueAt, s.Cron, s.TimeZoneId, s.IncludeSeconds)).ToList(),
            scheduled.Select(Job).ToList());

    public List<Communication.Responses.JobHistoryPoint> History(IEnumerable<Responses.JobHistoryPoint> history) =>
        history.Select(p => new Communication.Responses.JobHistoryPoint(p.Timestamp, p.Scheduled, p.Running, p.Succeeded, p.DeadLettered)).ToList();

    public List<LogEntry> Logs(IEnumerable<JobLogEntry> logs) =>
        logs.Select(l => new LogEntry(l.Id, l.Timestamp, l.Attempt, l.Level, l.Category, l.Message)).ToList();
}
