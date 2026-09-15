using Soenneker.Extensions.ValueTask;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private readonly LibrarianTable<string, LogBuffer> _logs;
    private readonly LibrarianTable<string, Node> _nodes;
    private readonly LibrarianTable<string, long> _metadata;

    public Task<bool> AppendLogs(JobLease lease, IReadOnlyList<JobLogMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages.Count is < 1 or > 50 || messages.Any(m => m.Message.Length > 4096 || m.Category.Length > 200 || m.Level.Length > 20))
            throw new ArgumentOutOfRangeException(nameof(messages));
        return Mutate(cancellationToken, async now =>
        {
            JobRecord? job = await Owned(lease, now);
            if (job is null) return false;
            if ((await _logs.GetEntry(job.Id)) is not { Value: var buffer } || buffer.ExpiresAt <= now)
                buffer = new LogBuffer();
            long sequence = (await _metadata.Get("logSequence"));
            foreach (JobLogMessage message in messages)
                buffer.Entries.Add(new JobLogEntry($"{now}-{++sequence}", now, job.Attempt, message.Level, message.Category, message.Message));
            if (buffer.Entries.Count > 1000) buffer.Entries.RemoveRange(0, buffer.Entries.Count - 1000);
            buffer.ExpiresAt = now + (long)(_retainCompletedJobs && HistoryRetention > TimeSpan.FromDays(30)
                ? HistoryRetention : TimeSpan.FromDays(30)).TotalMilliseconds;
            await _metadata.Set("logSequence", sequence).NoSync();
            await _logs.Set(job.Id, buffer).NoSync();
            return true;
        });
    }

    public Task<IReadOnlyList<JobLogEntry>> GetLogs(string jobId, int count = 200, CancellationToken cancellationToken = default)
    {
        ValidateId(jobId);
        ValidatePage(0, count);
        return Mutate<IReadOnlyList<JobLogEntry>>(cancellationToken, async now =>
            (await _logs.GetEntry(jobId)) is { Value: var buffer } && buffer.ExpiresAt > now ? buffer.Entries.TakeLast(count).ToArray() : []);
    }

    public Task<bool> SetProgress(JobLease lease, double percentage, string? message, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(percentage) || percentage is < 0 or > 100 || message?.Length > 500)
            throw new ArgumentOutOfRangeException();
        return Mutate(cancellationToken, async now =>
        {
            JobRecord? job = await Owned(lease, now);
            if (job is null) return false;
            await Save(job with { Progress = percentage, ProgressMessage = message, ProgressUpdatedAt = now }, now);
            return true;
        });
    }

    private async Task PruneServers(long now)
    {
        string[] expired = (await _nodes.Range("value.expiresAt", minimum: long.MinValue, maximum: now)).Select(p => p.Key).ToArray();
        if (expired.Length != 0) _notifyServers = true;
        foreach (string id in expired) await _nodes.Remove(id).NoSync();
    }

    public Task Heartbeat(string node, int workers, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ValidateId(node);
        if (workers is < 0 or > 256) throw new ArgumentOutOfRangeException(nameof(workers));
        long milliseconds = Duration(ttl);
        return Mutate(cancellationToken, async now =>
        {
            Node previous = await _nodes.Get(node);
            _notifyServers = previous.ExpiresAt <= now || previous.Workers != workers;
            await PruneServers(now);
            int busy = (await _jobs.Find("owner", node)).Count(j => j.State == JobState.Running && j.LeaseUntil > now);
            long bucket = now / 5000 * 5000;
            ServerWorkerHistoryPoint[] retained = (previous.WorkerHistory ?? [])
                .Where(point => point.Timestamp < bucket).ToArray();
            // Keep the preceding observation too, so the left edge has its last known count.
            ServerWorkerHistoryPoint[] history = retained.Where(point => point.Timestamp < bucket - 300000).TakeLast(1)
                .Concat(retained.Where(point => point.Timestamp >= bucket - 300000))
                .Append(new ServerWorkerHistoryPoint(bucket, busy, now + milliseconds)).ToArray();
            await _nodes.Set(node, new Node(now + milliseconds, workers) { WorkerHistory = history }).NoSync();
            return true;
        });
    }

    private async Task<WorkerServerView> Server(string node, Node data, long now, bool includeHistory = false) =>
        new(node, data.ExpiresAt, data.Workers, (await _jobs.Find("owner", node)).Where(j =>
            j.State == JobState.Running && j.LeaseUntil > now).OrderBy(j => j.LeaseUntil).ToArray())
        { WorkerHistory = includeHistory ? data.WorkerHistory ?? [] : [], ObservedAt = now };

    public Task<IReadOnlyList<WorkerServerView>> ListServers(int count = 200, CancellationToken cancellationToken = default)
    {
        ValidatePage(0, count);
        return Mutate<IReadOnlyList<WorkerServerView>>(cancellationToken, async now =>
            await Task.WhenAll((await _nodes.Range("value.expiresAt", minimum: now + 1)).OrderBy(p => p.Value.ExpiresAt).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(count).Select(p => Server(p.Key, p.Value, now))));
    }

    public Task<WorkerServerView?> GetServer(string node, CancellationToken cancellationToken = default)
    {
        ValidateId(node);
        return Mutate<WorkerServerView?>(cancellationToken, async now =>
            (await _nodes.GetEntry(node)) is { Value: var data } && data.ExpiresAt > now ? await Server(node, data, now, includeHistory: true) : null);
    }

    public Task<int> GetTotalWorkerCount(CancellationToken cancellationToken = default) =>
        Mutate(cancellationToken, async now => (await _nodes.Range("value.expiresAt", minimum: now + 1)).Sum(n => n.Value.Workers));

    public Task<long> GetRunningCount(CancellationToken cancellationToken = default) =>
        Mutate(cancellationToken, async _ => (long)await _jobs.Count("state", JobState.Running.Value));
}
