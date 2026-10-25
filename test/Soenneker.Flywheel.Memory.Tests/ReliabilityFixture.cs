using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Memory.Tests;

internal sealed class ReliabilityFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    public PartitionTestClock Clock { get; } = new();

    public ReliabilityFixture()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddFlywheel(options => { options.Workers = 2; options.PollInterval = TimeSpan.FromMilliseconds(50); }).AddMemory();
        services.AddSingleton<ReliabilityBlockingJob>();
        services.AddSingleton<IJobInvoker>(sp => sp.GetRequiredService<ReliabilityBlockingJob>());
        _provider = services.BuildServiceProvider();
    }

    public T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();
    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
