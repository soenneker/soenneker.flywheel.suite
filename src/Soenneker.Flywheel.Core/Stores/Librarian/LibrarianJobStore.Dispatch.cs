namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private static string? PartitionId(Communication.Dtos.JobRecord job) => job.Policy.PartitionKey is { } key
        ? $"{job.Name.Length}:{job.Name}{key}" : null;

    private async Task<int> RunningCount(string name, long now) => UseServerQueries
        ? await _running.Count(entry => entry.Value.Name == name && entry.Value.LeaseUntil > now)
        : (await _running.Find("name", name)).Count(entry => entry.LeaseUntil > now);

    private async Task<int> PartitionRunningCount(string partitionId, long now) => UseServerQueries
        ? await _running.Count(entry => entry.Value.PartitionId == partitionId && entry.Value.LeaseUntil > now)
        : (await _running.Find("partitionId", partitionId)).Count(entry => entry.LeaseUntil > now);

    private async IAsyncEnumerable<DispatchCandidate> Candidates(long now, string? version, string owner,
        IReadOnlySet<string>? jobNames, HashSet<string> blocked, HashSet<string> blockedPartitions, int pending)
    {
        // Old workers omit the derived ordering field. Fall back whenever any queued document lacks it,
        // so a rolling upgrade cannot hide work or change priority ordering.
        if (!UseServerQueries || await _dispatch.CountRange("value.order", minimum: "") != pending)
        {
            var entries = await _dispatch.Range("value.dueAt", maximum: now);
            DispatchCandidate[] eligible = entries.Select(entry => entry.Value)
                .Where(candidate => candidate.ApplicationVersion is null || candidate.ApplicationVersion == version)
                .Where(candidate => candidate.TargetNodeId is null || candidate.TargetNodeId == owner)
                .Where(candidate => jobNames is null || jobNames.Contains(candidate.Name))
                .OrderByDescending(candidate => candidate.Priority).ThenBy(candidate => candidate.DueAt)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal).ToArray();
            var turns = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string id in eligible.Where(candidate => candidate.PartitionId is not null).Select(candidate => candidate.PartitionId!).Distinct())
                turns[id] = await _partitionTurns.Get(id);
            // Keep method/priority positions and unkeyed ordering intact. Within each keyed method and
            // priority, the least recently served partition goes first, then its oldest due job.
            var fair = eligible.Where(candidate => candidate.PartitionId is not null)
                .GroupBy(candidate => (candidate.Name, candidate.Priority))
                .ToDictionary(group => group.Key, group => new Queue<DispatchCandidate>(group.OrderBy(candidate => turns[candidate.PartitionId!])));
            foreach (DispatchCandidate candidate in eligible)
                yield return candidate.PartitionId is null ? candidate : fair[(candidate.Name, candidate.Priority)].Dequeue();
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
            if (blockedPartitions.Count != 0)
            {
                string[] excludedPartitions = blockedPartitions.ToArray();
                query = query.Where(entry => !entry.Value.PartitionId!.StartsWith("", StringComparison.Ordinal) ||
                    !excludedPartitions.Contains(entry.Value.PartitionId));
            }
            var first = await _dispatch.First(query.OrderBy(entry => entry.Value.Order));
            if (first is null) yield break;
            DispatchCandidate selected = first.Value;
            if (selected.PartitionId is { } selectedPartition)
            {
                long selectedTurn = await _partitionTurns.Get(selectedPartition);
                string prefix = $"{selected.Name.Length}:{selected.Name}";
                // One fairness record per active partition; fetch at most one eligible job per partition,
                // never all jobs in a tenant's backlog. Priority and method ordering remain authoritative.
                var visited = new HashSet<string>(blockedPartitions, StringComparer.Ordinal) { selectedPartition };
                while (true)
                {
                    string[] excluded = visited.ToArray();
                    var partition = await _partitionTurns.First(_partitionTurns.Query()
                        .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) && entry.Value <= selectedTurn && !excluded.Contains(entry.Key))
                        .OrderBy(entry => entry.Value));
                    if (partition is null) break;
                    visited.Add(partition.Key);
                    string key = partition.Key;
                    string name = selected.Name;
                    int priority = selected.Priority;
                    var head = await _dispatch.First(query.Where(entry => entry.Value.Name == name &&
                        entry.Value.Priority == priority && entry.Value.PartitionId == key).OrderBy(entry => entry.Value.Order));
                    if (head is null) continue;
                    if (partition.Value < selectedTurn || StringComparer.Ordinal.Compare(head.Value.Order, selected.Order) < 0)
                    {
                        selected = head.Value;
                        selectedTurn = partition.Value;
                    }
                }
            }
            yield return selected;
        }
    }
}
