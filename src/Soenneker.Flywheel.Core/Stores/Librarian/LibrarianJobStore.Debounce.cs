using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    // Only short, cooperative external commits run under this lease. The database
    // transaction stores the lease; it never runs or retries the external callback.
    private static readonly TimeSpan DebounceCommitLimit = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan DebounceCommitLease = TimeSpan.FromMinutes(5);
    private readonly LibrarianTable<string, DebounceEntry> _debounce = new("debounce");

    private static void ValidateDebounceKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 1024) throw new ArgumentOutOfRangeException(nameof(key));
    }

    async Task IJobDebounceCoordinator.Enqueue(string key, string requestId, DateTimeOffset? requestedAt,
        EnqueueRequest request, CancellationToken cancellationToken)
    {
        ValidateDebounceKey(key);
        ValidateId(requestId);
        var job = Create(request);
        while (true)
        {
            bool complete = await Mutate(cancellationToken, async now =>
            {
                var previous = await _debounce.Get(key);
                if (previous?.RequestId == requestId) return true;
                long ticks = requestedAt?.UtcTicks ?? DateTimeOffset.FromUnixTimeMilliseconds(now).UtcTicks;
                if (requestedAt is not null && previous is not null &&
                    (previous.RequestedAtTicks > ticks || previous.RequestedAtTicks == ticks &&
                        string.CompareOrdinal(previous.RequestId, requestId) > 0)) return true;
                if (previous?.CommitUntil > now) return false;

                if (previous?.JobId is { } id && await _jobs.Get(id) is { } old && !Terminal(old.State))
                {
                    var cancelled = old with { CancelRequested = true };
                    if (old.State != JobState.Running)
                        cancelled = cancelled with { State = JobState.Cancelled, Version = old.Version + 1 };
                    await Save(cancelled, now);
                    await AdvanceChain(cancelled, now);
                }
                await Insert(job, (long)request.Delay.TotalMilliseconds, now);
                await _debounce.Set(key, new DebounceEntry(requestId, ticks, job.Id));
                return true;
            });
            if (complete) return;
            await Task.Delay(50, cancellationToken);
        }
    }

    ValueTask<string?> IJobDebounceCoordinator.Current(string key, CancellationToken cancellationToken)
    {
        ValidateDebounceKey(key);
        return new ValueTask<string?>(Mutate(cancellationToken, async _ => (await _debounce.Get(key))?.RequestId));
    }

    async ValueTask<bool> IJobDebounceCoordinator.Commit(string key, string? requestId,
        Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        ValidateDebounceKey(key);
        ArgumentNullException.ThrowIfNull(action);
        string token = Guid.NewGuid().ToString();
        while (true)
        {
            bool? acquired = await Mutate<bool?>(cancellationToken, async now =>
            {
                var entry = await _debounce.Get(key) ?? new DebounceEntry();
                if (entry.RequestId != requestId) return false;
                if (entry.CommitUntil > now) return null;
                await _debounce.Set(key, entry with { CommitToken = token, CommitUntil = now + (long)DebounceCommitLease.TotalMilliseconds });
                return true;
            });
            if (acquired == false) return false;
            if (acquired == true) break;
            await Task.Delay(50, cancellationToken);
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DebounceCommitLimit);
            timeout.Token.ThrowIfCancellationRequested();
            await action(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return true;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Mutate(cleanup.Token, async _ =>
            {
                var entry = await _debounce.Get(key);
                if (entry?.CommitToken == token)
                    await _debounce.Set(key, entry with { CommitToken = null, CommitUntil = 0 });
                return true;
            });
        }
    }
}
