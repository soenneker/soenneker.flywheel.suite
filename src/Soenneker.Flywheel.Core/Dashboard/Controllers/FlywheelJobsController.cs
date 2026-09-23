using Microsoft.AspNetCore.Http;
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
[Tags("Flywheel jobs")]
[Produces("application/json")]
[Route("[flywheel]/jobs")]
[Authorize(Policy = "FlywheelDashboard")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[TypeFilter(typeof(DashboardAntiforgeryFilter))]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class FlywheelJobsController(IJobStore store, IDashboardSnapshotFactory snapshots) : ControllerBase
{
    /// <summary>Queues an immediate execution without changing the recurring schedule.</summary>
    [HttpPost("schedules/{id}/run")]
    [EndpointSummary("Queues an immediate execution without changing the recurring schedule.")]
    [EndpointDescription("Requires a recurring schedule and store support for manual execution.")]
    [ProducesResponseType(typeof(StartedJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async ValueTask<IActionResult> RunRecurring([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return BadRequest();
        if (store is not IRecurringJobRunner runner)
            return StatusCode(501);
        string? jobId = await runner.RunRecurring(id, cancellationToken);
        return jobId is null ? NotFound() : Ok(new StartedJob(jobId));
    }

    /// <summary>Returns durable activity across all jobs for a requested UTC range.</summary>
    [HttpGet("history")]
    [EndpointSummary("Returns durable activity across all jobs for a requested UTC range.")]
    [EndpointDescription("Defaults to the last day within retention. Start must precede end and remain within retention; end may be at most five minutes in the future.")]
    [ProducesResponseType(typeof(IReadOnlyList<JobHistoryPoint>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async ValueTask<IActionResult> History(CancellationToken cancellationToken,
        [FromQuery] DateTimeOffset? startAt = null, [FromQuery] DateTimeOffset? endAt = null)
    {
        if (store is not IJobHistoryStore history)
            return StatusCode(501);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset end = endAt ?? now;
        DateTimeOffset earliest = now - history.HistoryRetention;
        DateTimeOffset start =
            startAt ?? (end - TimeSpan.FromDays(1) < earliest ? earliest : end - TimeSpan.FromDays(1));
        if (start >= end || end > now.AddMinutes(5) || start < earliest)
            return BadRequest();
        return Ok(snapshots.History(await history.GetHistory(start, end, cancellationToken)));
    }

    /// <summary>Returns matching retained job counts by current state and update time, independent of pagination.</summary>
    [HttpGet("history/search")]
    [EndpointSummary("Returns matching retained job counts by current state and update time, independent of pagination.")]
    [EndpointDescription("Query is limited to 200 characters. Supply both UTC range bounds or neither, with start before end. Requires search history support.")]
    [ProducesResponseType(typeof(IReadOnlyList<JobHistoryPoint>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async ValueTask<IActionResult> SearchHistory(CancellationToken cancellationToken, [FromQuery] string? q = null,
        [FromQuery] DateTimeOffset? startAt = null, [FromQuery] DateTimeOffset? endAt = null)
    {
        if (q?.Length > 200 || startAt.HasValue != endAt.HasValue || startAt >= endAt)
            return BadRequest();
        if (store is not IJobSearchHistoryStore history)
            return StatusCode(501);
        return Ok(await history.GetSearchHistory(q, startAt, endAt, cancellationToken));
    }

    /// <summary>Returns the configured aggregate activity retention.</summary>
    [HttpGet("history/options")]
    [EndpointSummary("Returns the configured aggregate activity retention.")]
    [EndpointDescription("Returns aggregate activity retention in seconds. Requires job history support.")]
    [ProducesResponseType(typeof(HistoryOptions), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public IActionResult HistoryOptions() => store is IJobHistoryStore history
        ? Ok(new HistoryOptions((long)history.HistoryRetention.TotalSeconds))
        : StatusCode(501);

    /// <summary>Returns the next 200 recurring schedules and pending executions independently of activity search.</summary>
    [HttpGet("schedules")]
    [EndpointSummary("Returns the next 200 recurring schedules and pending executions independently of activity search.")]
    [EndpointDescription("Returns up to 200 recurring schedules and 200 pending executions. Requires schedule query support.")]
    [ProducesResponseType(typeof(ScheduleView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async ValueTask<IActionResult> Schedules(CancellationToken cancellationToken)
    {
        if (store is not IJobScheduleStore schedules)
            return StatusCode(501);
        IReadOnlyList<RecurringJobView> recurring = await schedules.ListRecurring(200, cancellationToken);
        IReadOnlyList<JobRecord> scheduled = await schedules.ListScheduled(200, cancellationToken);
        return Ok(snapshots.Schedules(recurring, scheduled));
    }

    /// <summary>Returns a page of jobs without payloads or lease tokens.</summary>
    [HttpGet]
    [EndpointSummary("Returns a page of jobs without payloads or lease tokens.")]
    [EndpointDescription("Offset must be nonnegative. Count must be from 1 to 200 and defaults to 50. Payloads and lease credentials are excluded.")]
    [ProducesResponseType(typeof(IEnumerable<JobView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async ValueTask<IActionResult> List(CancellationToken cancellationToken, [FromQuery] int offset = 0,
        [FromQuery] int count = 50)
    {
        if (offset < 0 || count is < 1 or > 200)
            return BadRequest();
        return Ok((await store.List(offset, count, cancellationToken)).Select(snapshots.Job));
    }

    /// <summary>Searches jobs and returns the page and total match count.</summary>
    [HttpGet("search")]
    [EndpointSummary("Searches jobs and returns the page and total match count.")]
    [EndpointDescription("Query is limited to 200 characters. Offset must be nonnegative; count must be from 1 to 200. Supply both UTC range bounds or neither. Time filtering requires store support. excludedStates is a comma-separated list of job states.")]
    [ProducesResponseType(typeof(SearchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async ValueTask<IActionResult> Search(CancellationToken cancellationToken, [FromQuery] string? q = null,
        [FromQuery] int offset = 0, [FromQuery] int count = 50, [FromQuery] DateTimeOffset? startAt = null,
        [FromQuery] DateTimeOffset? endAt = null, [FromQuery] string? excludedStates = null)
    {
        if (!DashboardJobSearch.IsValid(excludedStates) || offset < 0 || count is < 1 or > 200 || q?.Length > 200 ||
            startAt.HasValue != endAt.HasValue || startAt >= endAt)
            return BadRequest();
        if (startAt.HasValue && store is not IJobTimeRangeSearchStore)
            return StatusCode(501);
        JobSearchResult result = await DashboardJobSearch.Search(store, q, offset, count, startAt, endAt,
            excludedStates, cancellationToken);
        return Ok(new SearchResult(result.Items.Select(snapshots.Job).ToList(), result.TotalCount));
    }

    /// <summary>Returns a bounded page of execution logs for an existing job.</summary>
    [HttpGet("{id}/logs")]
    [EndpointSummary("Returns a bounded page of execution logs for an existing job.")]
    [EndpointDescription("Returns retained logs for an existing job. Count must be from 1 to 200 and defaults to 200.")]
    [ProducesResponseType(typeof(IReadOnlyList<LogEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async ValueTask<IActionResult> GetLogs([FromRoute] string id, [FromServices] IJobLogStore logs,
        CancellationToken cancellationToken, [FromQuery] int count = 200)
    {
        if (count is < 1 or > 200 || string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return BadRequest();
        if (await store.Get(id, cancellationToken) is null)
            return NotFound();
        return Ok(snapshots.Logs(await logs.GetLogs(id, count, cancellationToken)));
    }

    /// <summary>Returns a job snapshot without its payload or lease token.</summary>
    [HttpGet("{id}")]
    [EndpointSummary("Returns a job snapshot without its payload or lease token.")]
    [EndpointDescription("Returns a retained job without payload or lease credentials. The identifier must be nonblank and at most 200 characters.")]
    [ProducesResponseType(typeof(JobView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async ValueTask<IActionResult> Get([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return BadRequest();
        JobRecord? job = await store.Get(id, cancellationToken);
        return job is null ? NotFound() : Ok(snapshots.Job(job));
    }

    /// <summary>Queues a fresh standalone execution from a finished job without changing the original execution or schedule.</summary>
    [HttpPost("{id}/run")]
    [EndpointSummary("Queues a fresh standalone execution from a finished job without changing the original execution or schedule.")]
    [EndpointDescription("Requires a succeeded, dead-lettered, or cancelled job. Other states return 409; jobs restricted to an application version return 501. Requires an antiforgery token.")]
    [ProducesResponseType(typeof(StartedJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async ValueTask<IActionResult> RunAgain([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return BadRequest();
        JobRecord? job = await store.Get(id, cancellationToken);
        if (job is null)
            return NotFound();
        if (job.State != Communication.Enums.JobState.Succeeded &&
            job.State != Communication.Enums.JobState.DeadLettered &&
            job.State != Communication.Enums.JobState.Cancelled)
            return Conflict();
        // Ordinary enqueue cannot preserve an exact-build restriction, and versioned enqueue
        // returns the original job. Never silently run version-bound work on another build.
        if (job.ApplicationVersion is not null)
            return StatusCode(501);
        string jobId =
            await store.Enqueue(
                new Communication.Requests.EnqueueRequest(job.Name, job.Payload, job.Policy, TimeSpan.Zero,
                    Description: job.Description), cancellationToken);
        return Ok(new StartedJob(jobId));
    }

    /// <summary>Requests cancellation of pending or running work.</summary>
    [HttpPost("{id}/cancel")]
    [EndpointSummary("Requests cancellation of pending or running work.")]
    [EndpointDescription("Requires an antiforgery token. Returns 409 when cancellation cannot be accepted by the store.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async ValueTask<IActionResult> Cancel([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return BadRequest();
        return await store.Cancel(id, cancellationToken) ? NoContent() : Conflict();
    }
}