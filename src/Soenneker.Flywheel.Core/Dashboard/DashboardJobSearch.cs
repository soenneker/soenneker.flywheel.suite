using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard;

/// <summary>Applies dashboard status exclusions before paging search results.</summary>
public static class DashboardJobSearch
{
    /// <summary>Checks a comma-separated list of display statuses.</summary>
    public static bool IsValid(string? excludedStates) => excludedStates is null ||
        excludedStates.Length <= 100 && excludedStates.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .All(state => state is "Scheduled" or "Queued" or "Running" or "Succeeded" or "DeadLettered" or "Cancelled" or "Waiting");

    /// <summary>Searches by text and optional date range, excluding statuses before counting and paging.</summary>
    public static async Task<JobSearchResult> Search(IJobStore store, string? query, int offset, int count,
        DateTimeOffset? startAt, DateTimeOffset? endAt, string? excludedStates, CancellationToken cancellationToken)
    {
        Task<JobSearchResult> Read(int skip, int take) => startAt is { } start && endAt is { } end
            ? ((IJobTimeRangeSearchStore)store).Search(query, start, end, skip, take, cancellationToken)
            : store.Search(query, skip, take, cancellationToken);
        if (string.IsNullOrEmpty(excludedStates)) return await Read(offset, count);
        var excluded = excludedStates.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var items = new List<JobRecord>(count);
        int matches = 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int skip = 0;;)
        {
            JobSearchResult page = await Read(skip, 200);
            foreach (JobRecord job in page.Items)
            {
                if (excluded.Contains(job.DisplayState(now))) continue;
                if (matches++ >= offset && items.Count < count) items.Add(job);
            }
            skip += page.Items.Count;
            if (page.Items.Count == 0 || skip >= page.TotalCount) break;
        }
        return new(items, matches);
    }
}
