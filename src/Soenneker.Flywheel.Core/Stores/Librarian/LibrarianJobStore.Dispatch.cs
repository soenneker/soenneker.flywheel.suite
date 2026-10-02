namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private async Task<int> RunningCount(string name, long now) => UseServerQueries
        ? await _running.Count(entry => entry.Value.Name == name && entry.Value.LeaseUntil > now)
        : (await _running.Find("name", name)).Count(entry => entry.LeaseUntil > now);

    private async IAsyncEnumerable<DispatchCandidate> Candidates(long now, string? version, string owner,
        IReadOnlySet<string>? jobNames, HashSet<string> blocked, int pending)
    {
        // Old workers omit the derived ordering field. Fall back whenever any queued document lacks it,
        // so a rolling upgrade cannot hide work or change priority ordering.
        if (!UseServerQueries || await _dispatch.CountRange("value.order", minimum: "") != pending)
        {
            var entries = await _dispatch.Range("value.dueAt", maximum: now);
            foreach (DispatchCandidate candidate in entries.Select(entry => entry.Value)
                .Where(candidate => candidate.ApplicationVersion is null || candidate.ApplicationVersion == version)
                .Where(candidate => candidate.TargetNodeId is null || candidate.TargetNodeId == owner)
                .Where(candidate => jobNames is null || jobNames.Contains(candidate.Name))
                .OrderByDescending(candidate => candidate.Priority).ThenBy(candidate => candidate.DueAt)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal))
                yield return candidate;
            yield break;
        }

        // The compound ordering field allows a bounded server page instead of loading the entire backlog.
        string[]? names = jobNames?.ToArray();
        while (true)
        {
            // Null fields are omitted by the storage serializer. Negating the string-presence range includes
            // both missing and explicit-null values; scalar equality to null only matches explicit nulls.
            var query = _dispatch.Query().Where(entry => entry.Value.DueAt <= now &&
                (!entry.Value.ApplicationVersion!.StartsWith("", StringComparison.Ordinal) || entry.Value.ApplicationVersion == version) &&
                (!entry.Value.TargetNodeId!.StartsWith("", StringComparison.Ordinal) || entry.Value.TargetNodeId == owner));
            if (names is not null) query = query.Where(entry => names.Contains(entry.Value.Name));
            if (blocked.Count != 0)
            {
                string[] excluded = blocked.ToArray();
                query = query.Where(entry => !excluded.Contains(entry.Value.Name));
            }
            var first = await _dispatch.First(query.OrderBy(entry => entry.Value.Order));
            if (first is null) yield break;
            yield return first.Value;
        }
    }
}
