using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Tests.Logging;

public sealed partial class JobLogTests
{
    private sealed class LogStore : IJobLogStore
    {
        public bool Fail;
        public ConcurrentBag<(string Id, JobLogMessage Message)> Entries = [];
        public Task<bool> AppendLogs(JobLease lease, IReadOnlyList<JobLogMessage> messages, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException();
            foreach (JobLogMessage message in messages) Entries.Add((lease.Job.Id, message));
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<JobLogEntry>> GetLogs(string jobId, int count = 200, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
