using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private static void ValidatePage(int offset, int count)
    {
        if (offset < 0 || count is < 1 or > 200) throw new ArgumentOutOfRangeException();
    }

    public Task<JobRecord?> Get(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return ReadSnapshot(cancellationToken, async _ => await _jobs.Get(id) is { } job ? await WithPayload(job) : null, pointReadsOnly: true);
    }

    private async ValueTask<JobRecord> WithPayload(JobRecord job) => job.Payload.Length != 0 ? job :
        job with { Payload = await _payloads.Get(job.Id) ?? throw new InvalidDataException("Missing immutable job payload.") };

    private static async Task<JobRecord[]> WithPayloads(JobRecord[] jobs, LibrarianTable<string, string> payloads)
    {
        string[] ids = jobs.Where(job => job.Payload.Length == 0).Select(job => job.Id).ToArray();
        if (ids.Length == 0) return jobs;
        string?[] values = await payloads.GetMany<string>(ids);
        int index = 0;
        for (int i = 0; i < jobs.Length; i++)
            if (jobs[i].Payload.Length == 0)
                jobs[i] = jobs[i] with { Payload = values[index++] ?? throw new InvalidDataException("Missing immutable job payload.") };
        return jobs;
    }

    public Task<IReadOnlyList<JobRecord>> List(int offset = 0, int count = 50, CancellationToken cancellationToken = default)
    {
        ValidatePage(offset, count);
        return ReadSnapshot<IReadOnlyList<JobRecord>>(cancellationToken, async _ =>
            await WithPayloads((await _jobs.Range("order", minimum: "", skip: offset, take: count)).Select(e => e.Value).ToArray(), _payloads));
    }

    public Task<IReadOnlyList<JobRecord>> ListScheduled(int count = 50, CancellationToken cancellationToken = default)
    {
        ValidatePage(0, count);
        return ReadSnapshot<IReadOnlyList<JobRecord>>(cancellationToken, async _ =>
            await WithPayloads((await _jobs.Range("scheduledOrder", minimum: "", take: count)).Select(e => e.Value).ToArray(), _payloads));
    }

    private static string ValidateQuery(string? query)
    {
        if (query?.Length > 200) throw new ArgumentOutOfRangeException(nameof(query));
        return query?.Trim() ?? "";
    }

    private static bool Matches(JobSearchDocument job, string query, long now, long? start, long? end) =>
        !(job.UpdatedAt < start || job.UpdatedAt >= end) && (query.Length == 0 ||
            job.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || job.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            job.DisplayState(now).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (job.Owner?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

    public Task<JobSearchResult> Search(string? query, int offset = 0, int count = 50, CancellationToken cancellationToken = default) =>
        SearchCore(query, null, null, offset, count, cancellationToken);

    public Task<JobSearchResult> Search(string? query, DateTimeOffset startAt, DateTimeOffset endAt,
        int offset = 0, int count = 50, CancellationToken cancellationToken = default)
    {
        if (startAt >= endAt) throw new ArgumentOutOfRangeException(nameof(startAt));
        return SearchCore(query, startAt.ToUnixTimeMilliseconds(), endAt.ToUnixTimeMilliseconds(), offset, count, cancellationToken);
    }

    public Task<JobSearchResult> Search(string? query, DateTimeOffset? startAt, DateTimeOffset? endAt,
        IReadOnlySet<string> excludedStates, int offset = 0, int count = 50, CancellationToken cancellationToken = default)
    {
        if (startAt.HasValue != endAt.HasValue || startAt >= endAt) throw new ArgumentOutOfRangeException(nameof(startAt));
        return SearchCore(query, startAt?.ToUnixTimeMilliseconds(), endAt?.ToUnixTimeMilliseconds(), offset, count, cancellationToken, excludedStates);
    }

    private Task<JobSearchResult> SearchCore(string? query, long? start, long? end, int offset, int count, CancellationToken ct,
        IReadOnlySet<string>? excludedStates = null)
    {
        ValidatePage(offset, count);
        string text = ValidateQuery(query);
        if (offset > 10000) throw new ArgumentOutOfRangeException(nameof(offset), "Search offsets cannot exceed 10000.");
        return SearchSnapshot(ct, async (table, payloads, now, token) =>
        {
            // The persisted order index is newest-updated first, with the job ID breaking ties.
            // Keep each group's requested prefix so running jobs precede other jobs before paging.
            if (text.Length == 0 && start is null && excludedStates is not { Count: > 0 })
            {
                int countAll = await table.CountAll();
                if (await table.CountRange("boardOrder", minimum: "") == countAll)
                {
                    var page = await table.Range<JobRecord>("boardOrder", minimum: "", skip: offset, take: count);
                    return new JobSearchResult(await WithPayloads(page.Select(entry => entry.Value).ToArray(), payloads), countAll);
                }
            }
            int limit = offset + count;
            var runningIds = new List<string>();
            var otherIds = new List<string>();
            int total = 0;
            await foreach (JobSearchDocument job in SearchCandidates(table, token))
            {
                if (!Matches(job, text, now, start, end) || excludedStates?.Contains(job.DisplayState(now)) == true) continue;
                List<string> group = job.State == JobState.Running ? runningIds : otherIds;
                if (group.Count < limit) group.Add(job.Id);
                total++;
            }
            string[] ids = runningIds.Concat(otherIds).Skip(offset).Take(count).ToArray();
            JobRecord?[] records = await table.GetMany<JobRecord>(ids);
            return new JobSearchResult(await WithPayloads(records.OfType<JobRecord>().ToArray(), payloads), total);
        });
    }
    public Task<IReadOnlyList<JobHistoryPoint>> GetSearchHistory(string? query, DateTimeOffset? startAt, DateTimeOffset? endAt,
        CancellationToken cancellationToken = default)
    {
        string text = ValidateQuery(query);
        if (startAt.HasValue != endAt.HasValue || startAt >= endAt) throw new ArgumentOutOfRangeException(nameof(startAt));
        return SearchSnapshot<IReadOnlyList<JobHistoryPoint>>(cancellationToken, async (table, payloads, now, token) =>
        {
            var buckets = new SortedDictionary<long, JobHistoryPoint>();
            await foreach (JobSearchDocument job in SearchCandidates(table, token))
            {
                if (!Matches(job, text, now, startAt?.ToUnixTimeMilliseconds(), endAt?.ToUnixTimeMilliseconds())) continue;
                long stamp = job.UpdatedAt / 300000 * 300000;
                JobHistoryPoint point = buckets.GetValueOrDefault(stamp) ?? new JobHistoryPoint(stamp, 0, 0, 0, 0);
                buckets[stamp] = job.State == JobState.Scheduled && job.DueAt <= now ? point with { Queued = point.Queued + 1 } :
                    Increment(point, job.State);
            }
            return buckets.Values.ToArray();
        });
    }
}

