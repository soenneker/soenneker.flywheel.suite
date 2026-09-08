using System.Threading.Channels;

namespace Soenneker.Flywheel.Core.Dashboard;

public sealed partial class DashboardSubscriptions
{
    private sealed class Subscription(
        DashboardSubscriptions owner,
        string connectionId,
        string kind,
        int version,
        string? query,
        int offset,
        int count,
        bool summary,
        string? jobId,
        DateTimeOffset? startAt,
        DateTimeOffset? endAt,
        CancellationTokenSource stop)
    {
        public string Kind => kind;
        public string? JobId => jobId;

        private readonly Channel<bool> _pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

        private Task _pump = Task.CompletedTask;

        public void Start()
        {
            Signal();
            _pump = Pump();
        }

        public void Signal() => _pending.Writer.TryWrite(true);

        public async Task Stop()
        {
            await stop.CancelAsync();
            _pending.Writer.TryComplete();
            await _pump;
            stop.Dispose();
        }

        private async Task Pump()
        {
            var failures = 0;
            try
            {
                while (await _pending.Reader.WaitToReadAsync(stop.Token))
                {
                    while (_pending.Reader.TryRead(out _))
                    {
                    }

                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        await owner.Send(connectionId, kind, version, query, offset, count, summary, jobId, startAt, endAt,
                            timeout.Token);
                        failures = 0;
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        owner.LogFailure(ex);
                        // Retry only failed synchronization, never read storage periodically while healthy/idle.
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures++, 5))), stop.Token);
                        Signal();
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
    }
}
