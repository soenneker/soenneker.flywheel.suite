using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed class SharedJobChangeFeed(Func<CancellationToken, IAsyncEnumerable<JobChange>> poll) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<Channel<JobChange>> _subscribers = [];
    private CancellationTokenSource? _source;
    private Task _pump = Task.CompletedTask;
    private bool _ready;
    private bool _disposed;

    internal async IAsyncEnumerable<JobChange> Watch([EnumeratorCancellation] CancellationToken token)
    {
        var channel = Channel.CreateBounded<JobChange>(64);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers.Add(channel);
            if (_ready) channel.Writer.TryWrite(JobChange.Resync);
            if (_source is null)
            {
                var source = new CancellationTokenSource();
                _source = source;
                _pump = Task.Run(() => Pump(source));
            }
        }
        try
        {
            await foreach (JobChange change in channel.Reader.ReadAllAsync(token)) yield return change;
        }
        finally
        {
            lock (_gate)
            {
                if (_subscribers.Remove(channel) && _subscribers.Count == 0)
                {
                    _source?.Cancel();
                    _source = null;
                    _ready = false;
                }
            }
        }
    }

    private async Task Pump(CancellationTokenSource source)
    {
        Exception? failure = null;
        try
        {
            await foreach (JobChange change in poll(source.Token))
            {
                lock (_gate)
                {
                    if (_source != source) break;
                    _ready = true;
                    foreach (var channel in _subscribers)
                    {
                        if (channel.Writer.TryWrite(change)) continue;
                        while (channel.Reader.TryRead(out _)) { }
                        channel.Writer.TryWrite(JobChange.Resync);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (_gate)
            {
                if (_source == source)
                {
                    foreach (var channel in _subscribers) channel.Writer.TryComplete(failure);
                    _subscribers.Clear();
                    _ready = false;
                    _source = null;
                }
                source.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _source?.Cancel();
            return new ValueTask(_pump);
        }
    }
}
