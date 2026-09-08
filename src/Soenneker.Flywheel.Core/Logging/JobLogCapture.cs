using Soenneker.Flywheel.Core.Stores.Abstract;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Logging;

/// <summary>Captures ILogger output within an asynchronous execution context without redirecting process-wide stdout.</summary>
public sealed partial class JobLogCapture : ILoggerProvider
{
    private static readonly AsyncLocal<Session?> Current = new();
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName);
    public void Dispose() { }

    /// <summary>Starts an ambient capture session for one leased attempt; dispose it to restore the prior context and drain buffered logs.</summary>
    /// <param name="lease">Current execution capability used to authorize log writes.</param>
    /// <param name="store">Destination for best-effort batched log writes.</param>
    public Session Begin(JobLease lease, IJobLogStore store)
    {
        var session = new Session(lease, store, Current.Value);
        Current.Value = session;
        return session;
    }
}
