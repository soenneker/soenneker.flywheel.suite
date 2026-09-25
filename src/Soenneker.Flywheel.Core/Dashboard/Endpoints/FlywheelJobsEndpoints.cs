using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Communication.Responses;
using Microsoft.AspNetCore.Mvc;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard.Endpoints;

/// <summary>Reads dashboard job snapshots and manages manual execution actions.</summary>
public static class FlywheelJobsEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/jobs/schedules/{id}/run", RunRecurring)
            .WithName("FlywheelJobs_RunRecurring")
            .WithTags("Flywheel jobs")
            .WithSummary("Queues an immediate execution without changing the recurring schedule.")
            .WithDescription("Requires a recurring schedule and store support for manual execution.")
            .Produces<StartedJob>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status501NotImplemented)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/jobs/history", History)
            .WithName("FlywheelJobs_History")
            .WithTags("Flywheel jobs")
            .WithSummary("Returns durable activity across all jobs for a requested UTC range.")
            .WithDescription("Defaults to the last day within retention. Start must precede end and remain within retention; end may be at most five minutes in the future.")
            .Produces<IReadOnlyList<JobHistoryPoint>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status501NotImplemented)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/jobs/history/search", SearchHistory)
            .WithName("FlywheelJobs_SearchHistory")
            .WithTags("Flywheel jobs")
            .WithSummary("Returns matching retained job counts by current state and update time, independent of pagination.")
            .WithDescription("Query is limited to 200 characters. Supply both UTC range bounds or neither, with start before end. Requires search history support.")
            .Produces<IReadOnlyList<JobHistoryPoint>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status501NotImplemented)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/jobs/history/options", HistoryOptions)
            .WithName("FlywheelJobs_HistoryOptions")
            .WithTags("Flywheel jobs")
            .WithSummary("Returns the configured aggregate activity retention.")
            .WithDescription("Returns aggregate activity retention in seconds. Requires job history support.")
            .Produces<HistoryOptions>()
            .Produces(StatusCodes.Status501NotImplemented)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/jobs/schedules", Schedules)
            .WithName("FlywheelJobs_Schedules")
            .WithTags("Flywheel jobs")
            .WithSummary("Returns the next 200 recurring schedules and pending executions independently of activity search.")
            .WithDescription("Returns up to 200 recurring schedules and 200 pending executions. Requires schedule query support.")
            .Produces<ScheduleView>()
            .Produces(StatusCodes.Status501NotImplemented)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/jobs", List)
            .WithName("FlywheelJobs_List")
            .WithTags("Flywheel jobs")
            .WithSummary("Returns a page of jobs without payloads or lease tokens.")
            .WithDescription("Offset must be nonnegative. Count must be from 1 to 200 and defaults to 50. Payloads and lease credentials are excluded.")
            .Produces<IEnumerable<JobView>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/jobs/search", Search)
            .WithName("FlywheelJobs_Search")
            .WithTags("Flywheel jobs")
            .WithSummary("Searches jobs and returns the page and total match count.")
            .WithDescription("Query is limited to 200 characters. Offset must be nonnegative; count must be from 1 to 200. Supply both UTC range bounds or neither. Time filtering requires store support. excludedStates is a comma-separated list of job states.")
            .Produces<SearchResult>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status501NotImplemented)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/jobs/{id}/logs", GetLogs)
            .WithName("FlywheelJobs_GetLogs")
            .WithTags("Flywheel jobs")
            .WithSummary("Returns a bounded page of execution logs for an existing job.")
            .WithDescription("Returns retained logs for an existing job. Count must be from 1 to 200 and defaults to 200.")
            .Produces<IReadOnlyList<LogEntry>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/jobs/{id}", Get)
            .WithName("FlywheelJobs_Get")
            .WithTags("Flywheel jobs")
            .WithSummary("Returns a job snapshot without its payload or lease token.")
            .WithDescription("Returns a retained job without payload or lease credentials. The identifier must be nonblank and at most 200 characters.")
            .Produces<JobView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapPost("/jobs/{id}/run", RunAgain)
            .WithName("FlywheelJobs_RunAgain")
            .WithTags("Flywheel jobs")
            .WithSummary("Queues a fresh standalone execution from a finished job without changing the original execution or schedule.")
            .WithDescription("Requires a succeeded, dead-lettered, or cancelled job. Other states return 409; jobs restricted to an application version return 501. Requires an antiforgery token.")
            .Produces<StartedJob>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status501NotImplemented)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapPost("/jobs/{id}/cancel", Cancel)
            .WithName("FlywheelJobs_Cancel")
            .WithTags("Flywheel jobs")
            .WithSummary("Requests cancellation of pending or running work.")
            .WithDescription("Requires an antiforgery token. Returns 409 when cancellation cannot be accepted by the store.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }

    /// <summary>Queues an immediate execution without changing the recurring schedule.</summary>
    public static async ValueTask<IResult> RunRecurring([FromServices] IJobStore store, [FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return TypedResults.BadRequest();
        if (store is not IRecurringJobRunner runner)
            return TypedResults.StatusCode(501);
        string? jobId = await runner.RunRecurring(id, cancellationToken);
        return jobId is null ? TypedResults.NotFound() : DashboardResults.Json(new StartedJob(jobId));
    }

    /// <summary>Returns durable activity across all jobs for a requested UTC range.</summary>
    public static async ValueTask<IResult> History([FromServices] IJobStore store, [FromServices] IDashboardSnapshotFactory snapshots, CancellationToken cancellationToken,
        [FromQuery] DateTimeOffset? startAt = null, [FromQuery] DateTimeOffset? endAt = null)
    {
        if (store is not IJobHistoryStore history)
            return TypedResults.StatusCode(501);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset end = endAt ?? now;
        DateTimeOffset earliest = now - history.HistoryRetention;
        DateTimeOffset start =
            startAt ?? (end - TimeSpan.FromDays(1) < earliest ? earliest : end - TimeSpan.FromDays(1));
        if (start >= end || end > now.AddMinutes(5) || start < earliest)
            return TypedResults.BadRequest();
        return DashboardResults.Json(snapshots.History(await history.GetHistory(start, end, cancellationToken)));
    }

    /// <summary>Returns matching retained job counts by current state and update time, independent of pagination.</summary>
    public static async ValueTask<IResult> SearchHistory([FromServices] IJobStore store, CancellationToken cancellationToken, [FromQuery] string? q = null,
        [FromQuery] DateTimeOffset? startAt = null, [FromQuery] DateTimeOffset? endAt = null)
    {
        if (q?.Length > 200 || startAt.HasValue != endAt.HasValue || startAt >= endAt)
            return TypedResults.BadRequest();
        if (store is not IJobSearchHistoryStore history)
            return TypedResults.StatusCode(501);
        return DashboardResults.Json(await history.GetSearchHistory(q, startAt, endAt, cancellationToken));
    }

    /// <summary>Returns the configured aggregate activity retention.</summary>
    public static IResult HistoryOptions([FromServices] IJobStore store) => store is IJobHistoryStore history
        ? DashboardResults.Json(new HistoryOptions((long)history.HistoryRetention.TotalSeconds))
        : TypedResults.StatusCode(501);

    /// <summary>Returns the next 200 recurring schedules and pending executions independently of activity search.</summary>
    public static async ValueTask<IResult> Schedules([FromServices] IJobStore store, [FromServices] IDashboardSnapshotFactory snapshots, CancellationToken cancellationToken)
    {
        if (store is not IJobScheduleStore schedules)
            return TypedResults.StatusCode(501);
        IReadOnlyList<RecurringJobView> recurring = await schedules.ListRecurring(200, cancellationToken);
        IReadOnlyList<JobRecord> scheduled = await schedules.ListScheduled(200, cancellationToken);
        return DashboardResults.Json(snapshots.Schedules(recurring, scheduled));
    }

    /// <summary>Returns a page of jobs without payloads or lease tokens.</summary>
    public static async ValueTask<IResult> List([FromServices] IJobStore store, [FromServices] IDashboardSnapshotFactory snapshots, CancellationToken cancellationToken, [FromQuery] int offset = 0,
        [FromQuery] int count = 50)
    {
        if (offset < 0 || count is < 1 or > 200)
            return TypedResults.BadRequest();
        return DashboardResults.Json((await store.List(offset, count, cancellationToken)).Select(snapshots.Job).ToList());
    }

    /// <summary>Searches jobs and returns the page and total match count.</summary>
    public static async ValueTask<IResult> Search([FromServices] IJobStore store, [FromServices] IDashboardSnapshotFactory snapshots, CancellationToken cancellationToken, [FromQuery] string? q = null,
        [FromQuery] int offset = 0, [FromQuery] int count = 50, [FromQuery] DateTimeOffset? startAt = null,
        [FromQuery] DateTimeOffset? endAt = null, [FromQuery] string? excludedStates = null)
    {
        if (!DashboardJobSearch.IsValid(excludedStates) || offset < 0 || count is < 1 or > 200 || q?.Length > 200 ||
            startAt.HasValue != endAt.HasValue || startAt >= endAt)
            return TypedResults.BadRequest();
        if (startAt.HasValue && store is not IJobTimeRangeSearchStore)
            return TypedResults.StatusCode(501);
        JobSearchResult result = await DashboardJobSearch.Search(store, q, offset, count, startAt, endAt,
            excludedStates, cancellationToken);
        return DashboardResults.Json(new SearchResult(result.Items.Select(snapshots.Job).ToList(), result.TotalCount));
    }

    /// <summary>Returns a bounded page of execution logs for an existing job.</summary>
    public static async ValueTask<IResult> GetLogs([FromServices] IJobStore store, [FromServices] IDashboardSnapshotFactory snapshots, [FromRoute] string id, [FromServices] IJobLogStore logs,
        CancellationToken cancellationToken, [FromQuery] int count = 200)
    {
        if (count is < 1 or > 200 || string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return TypedResults.BadRequest();
        if (await store.Get(id, cancellationToken) is null)
            return TypedResults.NotFound();
        return DashboardResults.Json(snapshots.Logs(await logs.GetLogs(id, count, cancellationToken)));
    }

    /// <summary>Returns a job snapshot without its payload or lease token.</summary>
    public static async ValueTask<IResult> Get([FromServices] IJobStore store, [FromServices] IDashboardSnapshotFactory snapshots, [FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return TypedResults.BadRequest();
        JobRecord? job = await store.Get(id, cancellationToken);
        return job is null ? TypedResults.NotFound() : DashboardResults.Json(snapshots.Job(job));
    }

    /// <summary>Queues a fresh standalone execution from a finished job without changing the original execution or schedule.</summary>
    public static async ValueTask<IResult> RunAgain([FromServices] IJobStore store, [FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return TypedResults.BadRequest();
        JobRecord? job = await store.Get(id, cancellationToken);
        if (job is null)
            return TypedResults.NotFound();
        if (job.State != Communication.Enums.JobState.Succeeded &&
            job.State != Communication.Enums.JobState.DeadLettered &&
            job.State != Communication.Enums.JobState.Cancelled)
            return TypedResults.Conflict();
        // Ordinary enqueue cannot preserve an exact-build restriction, and versioned enqueue
        // returns the original job. Never silently run version-bound work on another build.
        if (job.ApplicationVersion is not null)
            return TypedResults.StatusCode(501);
        string jobId =
            await store.Enqueue(
                new Communication.Requests.EnqueueRequest(job.Name, job.Payload, job.Policy, TimeSpan.Zero,
                    Description: job.Description), cancellationToken);
        return DashboardResults.Json(new StartedJob(jobId));
    }

    /// <summary>Requests cancellation of pending or running work.</summary>
    public static async ValueTask<IResult> Cancel([FromServices] IJobStore store, [FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            return TypedResults.BadRequest();
        return await store.Cancel(id, cancellationToken) ? TypedResults.NoContent() : TypedResults.Conflict();
    }
}