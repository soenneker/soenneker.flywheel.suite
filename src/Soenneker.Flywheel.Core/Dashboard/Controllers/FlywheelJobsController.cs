using Soenneker.Flywheel.Core.Dashboard.Filters;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Communication.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard.Controllers;

/// <summary>Reads dashboard job snapshots and manages manual execution actions.</summary>
[ApiController]
[Route("[flywheel]/jobs")]
[Authorize(Policy = "FlywheelDashboard")]
[TypeFilter(typeof(DashboardAntiforgeryFilter))]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class FlywheelJobsController(IJobStore store, IDashboardSnapshotFactory snapshots) : ControllerBase
{
    /// <summary>Queues an immediate execution without changing the recurring schedule.</summary>
    [HttpPost("schedules/{id}/run")]
    [ProducesResponseType(typeof(StartedJob), 200)]
    public async Task<IActionResult> RunRecurring(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return BadRequest();
        if (store is not IRecurringJobRunner runner) return StatusCode(501);
        string? jobId = await runner.RunRecurring(id, cancellationToken);
        return jobId is null ? NotFound() : Ok(new StartedJob(jobId));
    }

    /// <summary>Returns durable activity across all jobs for a requested UTC range.</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(List<Communication.Responses.JobHistoryPoint>), 200)]
    public async Task<IActionResult> History(CancellationToken cancellationToken, [FromQuery] DateTimeOffset? startAt = null,
        [FromQuery] DateTimeOffset? endAt = null)
    {
        if (store is not IJobHistoryStore history) return StatusCode(501);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset end = endAt ?? now;
        DateTimeOffset earliest = now - history.HistoryRetention;
        DateTimeOffset start = startAt ?? (end - TimeSpan.FromDays(1) < earliest ? earliest : end - TimeSpan.FromDays(1));
        if (start >= end || end > now.AddMinutes(5) || start < earliest) return BadRequest();
        return Ok(snapshots.History(await history.GetHistory(start, end, cancellationToken)));
    }

    /// <summary>Returns matching retained job counts by current state and update time, independent of pagination.</summary>
    [HttpGet("history/search")]
    [ProducesResponseType(typeof(IReadOnlyList<Communication.Responses.JobHistoryPoint>), 200)]
    public async Task<IActionResult> SearchHistory(CancellationToken cancellationToken, [FromQuery] string? q = null,
        [FromQuery] DateTimeOffset? startAt = null, [FromQuery] DateTimeOffset? endAt = null)
    {
        if (q?.Length > 200 || startAt.HasValue != endAt.HasValue || startAt >= endAt) return BadRequest();
        if (store is not IJobSearchHistoryStore history) return StatusCode(501);
        return Ok(await history.GetSearchHistory(q, startAt, endAt, cancellationToken));
    }

    /// <summary>Returns the configured aggregate activity retention.</summary>
    [HttpGet("history/options")]
    [ProducesResponseType(typeof(HistoryOptions), 200)]
    public IActionResult HistoryOptions() => store is IJobHistoryStore history
        ? Ok(new HistoryOptions((long)history.HistoryRetention.TotalSeconds))
        : StatusCode(501);

    /// <summary>Returns the next 200 recurring schedules and pending executions independently of activity search.</summary>
    [HttpGet("schedules")]
    [ProducesResponseType(typeof(ScheduleView), 200)]
    public async Task<IActionResult> Schedules(CancellationToken cancellationToken)
    {
        if (store is not IJobScheduleStore schedules) return StatusCode(501);
        IReadOnlyList<RecurringJobView> recurring = await schedules.ListRecurring(200, cancellationToken);
        IReadOnlyList<JobRecord> scheduled = await schedules.ListScheduled(200, cancellationToken);
        return Ok(snapshots.Schedules(recurring, scheduled));
    }

    /// <summary>Returns a page of jobs without payloads or lease tokens.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<JobView>), 200)]
    public async Task<IActionResult> List(CancellationToken cancellationToken, [FromQuery] int offset = 0, [FromQuery] int count = 50)
    {
        if (offset < 0 || count is < 1 or > 200) return BadRequest();
        return Ok((await store.List(offset, count, cancellationToken)).Select(snapshots.Job));
    }

    /// <summary>Searches jobs and returns the page and total match count.</summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(SearchResult), 200)]
    public async Task<IActionResult> Search(CancellationToken cancellationToken, [FromQuery] string? q = null,
        [FromQuery] int offset = 0, [FromQuery] int count = 50, [FromQuery] DateTimeOffset? startAt = null,
        [FromQuery] DateTimeOffset? endAt = null, [FromQuery] string? excludedStates = null)
    {
        if (!DashboardJobSearch.IsValid(excludedStates) || offset < 0 || count is < 1 or > 200 || q?.Length > 200 || startAt.HasValue != endAt.HasValue || startAt >= endAt) return BadRequest();
        if (startAt.HasValue && store is not IJobTimeRangeSearchStore) return StatusCode(501);
        JobSearchResult result = await DashboardJobSearch.Search(store, q, offset, count, startAt, endAt, excludedStates, cancellationToken);
        return Ok(new SearchResult(result.Items.Select(snapshots.Job).ToList(), result.TotalCount));
    }

    /// <summary>Returns a bounded page of execution logs for an existing job.</summary>
    [HttpGet("{id}/logs")]
    [ProducesResponseType(typeof(List<LogEntry>), 200)]
    public async Task<IActionResult> GetLogs(string id, [FromServices] IJobLogStore logs, CancellationToken cancellationToken,
        [FromQuery] int count = 200)
    {
        if (count is < 1 or > 200 || string.IsNullOrWhiteSpace(id) || id.Length > 200) return BadRequest();
        if (await store.Get(id, cancellationToken) is null) return NotFound();
        return Ok(snapshots.Logs(await logs.GetLogs(id, count, cancellationToken)));
    }

    /// <summary>Returns a job snapshot without its payload or lease token.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(JobView), 200)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return BadRequest();
        JobRecord? job = await store.Get(id, cancellationToken);
        return job is null ? NotFound() : Ok(snapshots.Job(job));
    }

    /// <summary>Queues a fresh standalone execution from a finished job without changing the original execution or schedule.</summary>
    [HttpPost("{id}/run")]
    [ProducesResponseType(typeof(StartedJob), 200)]
    public async Task<IActionResult> RunAgain(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return BadRequest();
        JobRecord? job = await store.Get(id, cancellationToken);
        if (job is null) return NotFound();
        if (job.State != Communication.Enums.JobState.Succeeded && job.State != Communication.Enums.JobState.DeadLettered &&
            job.State != Communication.Enums.JobState.Cancelled) return Conflict();
        // Ordinary enqueue cannot preserve an exact-build restriction, and versioned enqueue
        // returns the original job. Never silently run version-bound work on another build.
        if (job.ApplicationVersion is not null) return StatusCode(501);
        string jobId = await store.Enqueue(new Communication.Requests.EnqueueRequest(job.Name, job.Payload, job.Policy,
            TimeSpan.Zero, Description: job.Description), cancellationToken);
        return Ok(new StartedJob(jobId));
    }

    /// <summary>Requests cancellation of pending or running work.</summary>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Cancel(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return BadRequest();
        return await store.Cancel(id, cancellationToken) ? NoContent() : Conflict();
    }

}
