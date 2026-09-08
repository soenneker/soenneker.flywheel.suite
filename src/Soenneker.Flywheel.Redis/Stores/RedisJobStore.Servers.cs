using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Enums;
using Soenneker.Flywheel.Core.Responses;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    public async Task<int> GetTotalWorkerCount(CancellationToken cancellationToken = default)
    {
        IDatabase db = await Database(cancellationToken);
        long now = await Time(db, cancellationToken);
        RedisValue[] nodes = await db.SortedSetRangeByScoreAsync(Nodes, start: now).WaitAsync(cancellationToken);
        if (nodes.Length == 0) return 0;
        RedisValue[] workers = await db.HashGetAsync(NodeWorkers, nodes).WaitAsync(cancellationToken);
        var total = 0;
        foreach (RedisValue value in workers)
            if (value.TryParse(out int count)) total = checked(total + count);
        return total;
    }

    public async Task<IReadOnlyList<ServerView>> ListServers(int count = 200,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(count));
        IDatabase db = await Database(cancellationToken);
        long now = await Time(db, cancellationToken);
        SortedSetEntry[] entries = await db.SortedSetRangeByScoreWithScoresAsync(Nodes, start: now,
            order: Order.Ascending, take: count).WaitAsync(cancellationToken);
        var nodeIds = entries.Select(entry => (RedisValue)entry.Element).ToArray();
        RedisValue[] workers = nodeIds.Length == 0 ? [] : await db.HashGetAsync(NodeWorkers, nodeIds).WaitAsync(cancellationToken);
        Dictionary<string, List<JobRecord>> jobs = await RunningJobsByOwner(db, now, cancellationToken);
        var result = new ServerView[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            string id = entries[i].Element!;
            result[i] = new ServerView(id, (long)entries[i].Score, workers[i].TryParse(out int workerCount) ? workerCount : 0,
                jobs.TryGetValue(id, out List<JobRecord>? owned) ? owned : []);
        }
        return result;
    }

    public async Task<ServerView?> GetServer(string node, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(node) || node.Length > 200) throw new ArgumentException("Invalid server ID.", nameof(node));
        IDatabase db = await Database(cancellationToken);
        Task<long> timeTask = Time(db, cancellationToken);
        Task<double?> scoreTask = db.SortedSetScoreAsync(Nodes, node);
        Task<RedisValue> workersTask = db.HashGetAsync(NodeWorkers, node);
        await Task.WhenAll(timeTask, scoreTask, workersTask).WaitAsync(cancellationToken);
        long now = timeTask.Result;
        if (scoreTask.Result is not { } expiresAt || expiresAt <= now) return null;
        Dictionary<string, List<JobRecord>> jobs = await RunningJobsByOwner(db, now, cancellationToken);
        return new ServerView(node, (long)expiresAt, workersTask.Result.TryParse(out int workerCount) ? workerCount : 0,
            jobs.TryGetValue(node, out List<JobRecord>? owned) ? owned : []);
    }

    private async Task<Dictionary<string, List<JobRecord>>> RunningJobsByOwner(IDatabase db, long now,
        CancellationToken cancellationToken)
    {
        RedisValue[] ids = await db.SortedSetRangeByScoreAsync(Running, start: now, exclude: Exclude.Start)
                                   .WaitAsync(cancellationToken);
        JobRecord[] records = await ReadJobs(db, ids, cancellationToken);
        var result = new Dictionary<string, List<JobRecord>>(StringComparer.Ordinal);
        foreach (JobRecord job in records)
        {
            if (job.State != JobState.Running || job.LeaseUntil <= now || string.IsNullOrEmpty(job.Owner)) continue;
            if (!result.TryGetValue(job.Owner, out List<JobRecord>? ownerJobs))
                result[job.Owner] = ownerJobs = [];
            ownerJobs.Add(job);
        }
        return result;
    }
}
