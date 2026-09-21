using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Memory.Tests;

internal sealed class DebounceTestInvoker : IJobInvoker
{
    public string Name => "debounce-test";
    public Task Invoke(IServiceProvider services, string payload, CancellationToken cancellationToken) => Task.CompletedTask;
}
