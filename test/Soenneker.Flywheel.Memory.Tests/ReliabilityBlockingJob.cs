using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Memory.Tests;

internal sealed class ReliabilityBlockingJob : IJobInvoker
{
    public string Name => "reliability.blocking";
    public int Started;
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task Invoke(IServiceProvider services, string payload, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Started);
        await Release.Task.WaitAsync(cancellationToken);
    }
}
