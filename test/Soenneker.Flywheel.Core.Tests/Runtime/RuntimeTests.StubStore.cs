using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Requests;
using Soenneker.Flywheel.Core.Responses;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Tests.Runtime;

public sealed partial class RuntimeTests
{
    private sealed class StubStore : IJobStore, IJobChangeFeed
    {
        private readonly Channel<JobChange> _changes = Channel.CreateUnbounded<JobChange>();
        public Enums.LeaseStatus Status;
        public bool StalledRenewal;
        public int Commits;
        public Enums.JobOutcome Outcome;
        public Task<JobLease?> Claim(string owner, TimeSpan duration, CancellationToken cancellationToken = default) =>
            Task.FromResult<JobLease?>(new JobLease(new JobRecord { Id = "one", Name = "test", Payload = "{}", Attempt = 1,
                Policy = new JobPolicy { Timeout = TimeSpan.FromMilliseconds(150) } }, "token", 1));
        public Task<Enums.LeaseStatus> Renew(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default) =>
            StalledRenewal ? new TaskCompletionSource<Enums.LeaseStatus>().Task : Task.FromResult(Status);
        public Task<bool> Finish(JobLease lease, Enums.JobOutcome outcome, string? error, TimeSpan retryDelay, CancellationToken cancellationToken = default)
        { Commits++; Outcome = outcome; return Task.FromResult(true); }
        public Task<string> Enqueue(EnqueueRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> Cancel(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Maintain(int batchSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobRecord?> Get(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobRecord>> List(int offset = 0, int count = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobSearchResult> Search(string? query, int offset = 0, int count = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AddRecurring(string id, EnqueueRequest request, TimeSpan interval, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<JobChange> Watch(CancellationToken cancellationToken = default) => _changes.Reader.ReadAllAsync(cancellationToken);
        public void Notify() => _changes.Writer.TryWrite(new JobChange("Job", "one"));
    }
}
