using Soenneker.Flywheel.Core.Enums;
using Soenneker.Flywheel.Core.Logging.Dtos;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Responses;
using Soenneker.Flywheel.Core.Requests;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Soenneker.Flywheel.Core;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    private sealed class SearchStore : IJobStore, IJobLogStore, IJobChangeFeed
    {
        public readonly System.Threading.Channels.Channel<JobChange> Changes = System.Threading.Channels.Channel.CreateUnbounded<JobChange>();
        public readonly TaskCompletionSource Subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<JobChange> Watch([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return JobChange.Resync;
            Subscribed.TrySetResult();
            await foreach (var change in Changes.Reader.ReadAllAsync(cancellationToken)) yield return change;
        }
        public int Calls, Offset, Count;
        public string? Query;
        public Task<JobSearchResult> Search(string? query, int offset = 0, int count = 50, CancellationToken cancellationToken = default)
        {
            Calls++; Query = query; Offset = offset; Count = count;
            return Task.FromResult(new JobSearchResult([new JobRecord { Id = "one", Name = "invoice", Payload = "private-payload", Token = "private-token", Policy = new() }], 51));
        }
        public Task<string> Enqueue(EnqueueRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobLease?> Claim(string owner, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LeaseStatus> Renew(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> Finish(JobLease lease, JobOutcome outcome, string? error, TimeSpan retryDelay, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> Cancel(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Maintain(int batchSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobRecord?> Get(string id, CancellationToken cancellationToken = default) => Task.FromResult<JobRecord?>(id == "one" ? new() { Id = id, Name = "test", Payload = "{}", Policy = new() } : null);
        public Task<bool> AppendLogs(JobLease lease, IReadOnlyList<JobLogMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobLogEntry>> GetLogs(string jobId, int count = 200, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JobLogEntry>>([new("1-0", 1, 1, "Information", "test", "message")]);
        public Task<IReadOnlyList<JobRecord>> List(int offset = 0, int count = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AddRecurring(string id, EnqueueRequest request, TimeSpan interval, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
