using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    private sealed class SearchStore : IJobStore, IJobLogStore, IJobChangeFeed, INodeStore, IServerStore
    {
        public readonly System.Threading.Channels.Channel<JobChange> Changes = System.Threading.Channels.Channel.CreateUnbounded<JobChange>();
        public readonly TaskCompletionSource Subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<JobChange> Watch([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return JobChange.Resync;
            Subscribed.TrySetResult();
            await foreach (JobChange change in Changes.Reader.ReadAllAsync(cancellationToken)) yield return change;
        }
        public JobRecord[]? SearchItems;
        public int Calls, Offset, Count;
        public string? Query;
        public Task<JobSearchResult> Search(string? query, int offset = 0, int count = 50, CancellationToken cancellationToken = default)
        {
            Calls++; Query = query; Offset = offset; Count = count;
            if (SearchItems is { } source)
            {
                var matches = source.Where(job => string.IsNullOrEmpty(query) || job.Name.Contains(query)).ToArray();
                return Task.FromResult(new JobSearchResult(matches.Skip(offset).Take(count).ToArray(), matches.Length));
            }
            return Task.FromResult(new JobSearchResult([new JobRecord { Id = "one", Name = "invoice", Payload = "private-payload", Token = "private-token", Policy = new() }], 51));
        }
        public Task<string> Enqueue(EnqueueRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobLease?> Claim(string owner, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LeaseStatus> Renew(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> Finish(JobLease lease, JobOutcome outcome, string? error, TimeSpan retryDelay, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> Cancel(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Maintain(int batchSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Heartbeat(string node, int workers, TimeSpan ttl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkerServerView>> ListServers(int count = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkerServerView>>([new("node one", 123, 12, [])]);
        public Task<WorkerServerView?> GetServer(string node, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkerServerView?>(node == "node one" ? new(node, 123, 12, []) : null);
        public Task<int> GetTotalWorkerCount(CancellationToken cancellationToken = default) => Task.FromResult(12);
        public Task<JobRecord?> Get(string id, CancellationToken cancellationToken = default) => Task.FromResult<JobRecord?>(id == "one" ? new() { Id = id, Name = "test", Payload = "{}", Policy = new() } : null);
        public Task<bool> AppendLogs(JobLease lease, IReadOnlyList<JobLogMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobLogEntry>> GetLogs(string jobId, int count = 200, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JobLogEntry>>([new("1-0", 1, 1, "Information", "test", "message")]);
        public Task<IReadOnlyList<JobRecord>> List(int offset = 0, int count = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AddRecurring(string id, EnqueueRequest request, TimeSpan interval, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
