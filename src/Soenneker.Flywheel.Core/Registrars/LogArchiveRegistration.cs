using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Librarian.Abstractions;
using Soenneker.Flywheel.Core.Services.Abstract;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Soenneker.Flywheel.Core.Registrars;

/// <summary>Optional archival registration for Flywheel storage providers.</summary>
public static class LogArchiveRegistration
{
    /// <summary>Registers archival using a fresh owned Librarian database per file.
    /// Configure Enabled to opt in. The factory must map each file ID to a stable, dedicated storage address,
    /// isolated from runtime storage and other deployments. Archives remain until externally removed.</summary>
    public static FlywheelBuilder AddLogArchive(this FlywheelBuilder builder,
        Func<IServiceProvider, string, CancellationToken, ValueTask<ILibrarianDatabase>> databaseFactory,
        Action<LogArchiveOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(databaseFactory);
        return builder.AddLogArchive(sp => new LibrarianJobLogArchive((id, ct) => databaseFactory(sp, id, ct)), configure);
    }

    /// <summary>Registers a custom durable archive destination. Configure Enabled to opt in.
    /// All workers sharing runtime storage must use the same archive settings and destination.</summary>
    public static FlywheelBuilder AddLogArchive(this FlywheelBuilder builder,
        Func<IServiceProvider, IJobLogArchive> archiveFactory, Action<LogArchiveOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(archiveFactory);
        var options = new LogArchiveOptions();
        configure?.Invoke(options);
        options.Validate();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(archiveFactory);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobInvoker, LogArchiveJobInvoker>());
        builder.Services.AddHostedService<LogArchiveService>();
        return builder;
    }
}
