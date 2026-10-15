using System.Threading.Channels;
using Soenneker.Atomics.ValueInts;

namespace Soenneker.Flywheel.Core.Services;

internal sealed class WorkerPoolState(string name, int workers)
{
    internal string Name { get; } = name;
    internal ValueAtomicInt WorkerCount = new(workers);
    internal Channel<byte> Pending { get; } = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite });
    internal void Pulse() => Pending.Writer.TryWrite(0);
}
