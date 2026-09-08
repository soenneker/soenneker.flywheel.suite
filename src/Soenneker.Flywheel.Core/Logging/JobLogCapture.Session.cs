using Soenneker.Flywheel.Core.Stores.Abstract;
using System.Threading.Channels;
using Soenneker.Flywheel.Core.Logging.Dtos;
using Soenneker.Flywheel.Core.Dtos;

namespace Soenneker.Flywheel.Core.Logging;

public sealed partial class JobLogCapture
{
    /// <summary>Buffers one attempt's diagnostic messages without blocking its handler; disposal allows up to two seconds to drain.</summary>
    public sealed class Session : IAsyncDisposable
    {
        private readonly Channel<JobLogMessage> _pending = Channel.CreateBounded<JobLogMessage>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _stop = new();
        private readonly Session? _previous;
        private readonly Task _pump;
        private int _dropped;
        internal Session(JobLease lease, IJobLogStore store, Session? previous)
        {
            _previous = previous;
            // Start before assigning the ambient context to prevent recursively capturing storage-provider logs.
            _pump = Pump(lease, store);
        }
        /// <summary>Queues a diagnostic message, truncating category to 200 characters and message to 4096; a full buffer drops the entry.</summary>
        public void Write(string level, string category, string message)
        {
            if (!_pending.Writer.TryWrite(new JobLogMessage(level, category[..Math.Min(category.Length, 200)], message[..Math.Min(message.Length, 4096)])))
                Interlocked.Increment(ref _dropped);
        }
        private async Task Pump(JobLease lease, IJobLogStore store)
        {
            try
            {
                var batch = new List<JobLogMessage>(50);
                while (await _pending.Reader.WaitToReadAsync(_stop.Token))
                {
                    batch.Clear();
                    int dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0) batch.Add(new JobLogMessage("Warning", "Flywheel", $"{dropped} log lines dropped because the buffer was full."));
                    while (batch.Count < 50 && _pending.Reader.TryRead(out JobLogMessage? entry)) batch.Add(entry);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    if (!await store.AppendLogs(lease, batch, deadline.Token).WaitAsync(deadline.Token)) return;
                }
            }
            catch { /* Best effort diagnostics: storage outages must not change job outcomes. */ }
            finally { _pending.Writer.TryComplete(); }
        }
        public async ValueTask DisposeAsync()
        {
            Current.Value = _previous;
            _pending.Writer.TryComplete();
            _stop.CancelAfter(TimeSpan.FromSeconds(2));
            await _pump;
            _stop.Dispose();
        }
    }
}
