using Microsoft.Extensions.Logging;
using Soenneker.Librarian.Memory;
using Soenneker.Flywheel.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Memory;

/// <summary>Registers process-local Flywheel storage. Data is lost when the process stops.</summary>
public static class MemoryRegistration
{
    /// <summary>The DI key for Flywheel's Memory storage dependencies.</summary>
    public const string LibrarianServiceKey = "Flywheel.Memory";

    /// <summary>Adds one shared memory store to this service provider. Separate providers do not share jobs or limits.</summary>
    public static FlywheelBuilder AddMemory(this FlywheelBuilder builder, Action<FlywheelMemoryOptions>? configure = null)
    {
        var options = new FlywheelMemoryOptions();
        configure?.Invoke(options);
        MemoryJobStore.ValidateOptions(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddKeyedSingleton<MemoryLibrarianDatabase>(LibrarianServiceKey,
            (sp, _) => new MemoryLibrarianDatabase(sp.GetRequiredService<ILogger<MemoryLibrarianDatabase>>()));
        builder.Services.AddSingleton(sp => new MemoryJobStore(
            sp.GetRequiredKeyedService<MemoryLibrarianDatabase>(LibrarianServiceKey),
            sp.GetRequiredService<FlywheelMemoryOptions>(), sp.GetService<TimeProvider>(), sp.GetService<FlywheelOptions>()));
        builder.Services.AddHostedService<MemoryLiveActivityRecorder>();
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<MemoryJobStore>());
        builder.Services.AddSingleton<IJobChangeFeed>(sp => sp.GetRequiredService<MemoryJobStore>());
        builder.Services.AddSingleton<ICronJobStore>(sp => sp.GetRequiredService<MemoryJobStore>());
        builder.Services.AddSingleton<IJobChainStore>(sp => sp.GetRequiredService<MemoryJobStore>());
        builder.Services.AddSingleton<IMethodPolicyStore>(sp => sp.GetRequiredService<MemoryJobStore>());
        builder.Services.AddSingleton<INodeStore>(sp => sp.GetRequiredService<MemoryJobStore>());
        builder.Services.AddSingleton<IJobLogStore>(sp => sp.GetRequiredService<MemoryJobStore>());
        builder.Services.AddSingleton<IJobProgressStore>(sp => sp.GetRequiredService<MemoryJobStore>());
        builder.Services.AddSingleton<IServerStore>(sp => sp.GetRequiredService<MemoryJobStore>());
        return builder;
    }
}
