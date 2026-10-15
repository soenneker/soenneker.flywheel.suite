using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Core.Tests.Runtime;

internal sealed class PoolTestExecutor(bool block) : IJobExecutor
{
    internal ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

    public Task<bool> RunOnce(CancellationToken cancellationToken) => RunOnce("default", () => { }, cancellationToken, cancellationToken);
    public Task<bool> RunOnce(Action onClaimed, CancellationToken cancellationToken) => RunOnce("default", onClaimed, cancellationToken, cancellationToken);

    public async Task<bool> RunOnce(string pool, Action onClaimed, CancellationToken claimingToken, CancellationToken executionToken)
    {
        Calls.AddOrUpdate(pool, 1, (_, count) => count + 1);
        if (!block) return false;
        onClaimed();
        await Task.Delay(Timeout.InfiniteTimeSpan, executionToken);
        return true;
    }
}
