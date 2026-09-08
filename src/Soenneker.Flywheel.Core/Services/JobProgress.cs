using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Services;

public sealed class JobProgress(IJobStore store) : IJobProgress
{
    private readonly AsyncLocal<JobLease?> _current = new();

    internal IDisposable Begin(JobLease lease)
    {
        JobLease? previous = _current.Value;
        _current.Value = lease;
        return new Scope(this, previous);
    }

    public async ValueTask Report(double percentage, string? message = null,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(percentage) || percentage is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percentage));
        if (message?.Length > 500)
            throw new ArgumentOutOfRangeException(nameof(message));
        JobLease lease = _current.Value ?? throw new InvalidOperationException("Progress can only be reported while a Flywheel job is executing.");
        if (store is not IJobProgressStore progressStore)
            throw new NotSupportedException("The job store does not support progress reporting.");
        if (!await progressStore.SetProgress(lease, percentage, message, cancellationToken))
            throw new InvalidOperationException("The job no longer owns its execution lease.");
    }

    private sealed class Scope(JobProgress owner, JobLease? previous) : IDisposable
    {
        public void Dispose() => owner._current.Value = previous;
    }
}
