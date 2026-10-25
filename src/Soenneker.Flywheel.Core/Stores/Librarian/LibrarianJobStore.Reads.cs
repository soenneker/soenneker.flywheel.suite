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
        return Mutate(cancellationToken, async _ => (await _jobs.Get(id)));
    }

    private static readonly IComparer<JobRecord> RunningFirst = Comparer<JobRecord>.Create((a, b) =>
    {
        int compared = (b.State == JobState.Running).CompareTo(a.State == JobState.Running);
        if (compared != 0) return compared;
        compared = b.CreatedAt.CompareTo(a.CreatedAt);
        return compared != 0 ? compared : StringComparer.Ordinal.Compare(b.Id, a.Id);
    });

    private static T[] Page<T>(IEnumerable<T> source, IComparer<T> comparer, int offset, int count, out int total)
    {
        // Keep only the requested prefix while enumerating Librarian records, rather than sorting the entire database.
        var selected = new PriorityQueue<T, T>(Comparer<T>.Create((a, b) => comparer.Compare(b, a)));
        total = 0;
        long limit = (long)offset + count;
        foreach (T item in source)
        {
            total++;
            if (selected.Count < limit) selected.Enqueue(item, item);
            else if (comparer.Compare(item, selected.Peek()) < 0) selected.DequeueEnqueue(item, item);
        }
        return selected.UnorderedItems.Select(p => p.Element).Order(comparer).Skip(offset).ToArray();
    }

    public Task<IReadOnlyList<JobRecord>> List(int offset = 0, int count = 50, CancellationToken cancellationToken = default)
    {
        ValidatePage(offset, count);
        return Mutate<IReadOnlyList<JobRecord>>(cancellationToken, async _ =>
            (await _jobs.Range("order", minimum: "", skip: offset, take: count)).Select(e => e.Value).ToArray());
    }

    public Task<IReadOnlyList<JobRecord>> ListScheduled(int count = 50, CancellationToken cancellationToken = default)
    {
        ValidatePage(0, count);
        return Mutate<IReadOnlyList<JobRecord>>(cancellationToken, async _ =>
            (await _jobs.Range("scheduledOrder", minimum: "", take: count)).Select(e => e.Value).ToArray());
    }

    private static string ValidateQuery(string? query)
    {
        if (query?.Length > 200) throw new ArgumentOutOfRangeException(nameof(query));
        return query?.Trim() ?? "";
    }

    private static bool Matches(JobRecord job, string query, long now, long? start, long? end) =>
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
        return SearchSnapshot(ct, async (table, now, token) =>
        {
            var selected = new PriorityQueue<JobRecord, JobRecord>(Comparer<JobRecord>.Create((a, b) => RunningFirst.Compare(b, a)));
            int total = 0;
            await foreach (JobRecord job in SearchCandidates(table, token))
            {
                if (!Matches(job, text, now, start, end) || excludedStates?.Contains(job.DisplayState(now)) == true) continue;
                total++;
                if (selected.Count < offset + count) selected.Enqueue(job, job);
                else if (RunningFirst.Compare(job, selected.Peek()) < 0) selected.DequeueEnqueue(job, job);
            }
            string[] ids = selected.UnorderedItems.Select(p => p.Element).Order(RunningFirst).Skip(offset).Select(j => j.Id).ToArray();
            JobRecord?[] records = await table.GetMany<JobRecord>(ids);
            return new JobSearchResult(records.OfType<JobRecord>().ToArray(), total);
        });
    }
    public Task<IReadOnlyList<JobHistoryPoint>> GetSearchHistory(string? query, DateTimeOffset? startAt, DateTimeOffset? endAt,
        CancellationToken cancellationToken = default)
    {
        string text = ValidateQuery(query);
        if (startAt.HasValue != endAt.HasValue || startAt >= endAt) throw new ArgumentOutOfRangeException(nameof(startAt));
        return SearchSnapshot<IReadOnlyList<JobHistoryPoint>>(cancellationToken, async (table, now, token) =>
        {
            var buckets = new SortedDictionary<long, JobHistoryPoint>();
            await foreach (JobRecord job in SearchCandidates(table, token))
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

