using Microsoft.Extensions.DependencyInjection;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.MemoryStream.Registrars;

using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Filesystem;

/// <summary>Registers filesystem-backed Flywheel storage using Librarian documents.</summary>
public static class FilesystemRegistration
{
    /// <summary>Adds a filesystem database owned exclusively by this runtime.</summary>
    public static FlywheelBuilder AddFilesystem(this FlywheelBuilder builder, Action<FlywheelFilesystemOptions>? configure = null)
    {
        var options = new FlywheelFilesystemOptions();
        configure?.Invoke(options);
        FilesystemJobStore.ValidateOptions(options);
        builder.Services.AddFileUtilAsSingleton().AddMemoryStreamUtilAsSingleton();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<FilesystemJobStore>();
        builder.Services.AddHostedService<FilesystemLiveActivityRecorder>();
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<FilesystemJobStore>());
        builder.Services.AddSingleton<IJobChangeFeed>(sp => sp.GetRequiredService<FilesystemJobStore>());
        builder.Services.AddSingleton<ICronJobStore>(sp => sp.GetRequiredService<FilesystemJobStore>());
        builder.Services.AddSingleton<IJobChainStore>(sp => sp.GetRequiredService<FilesystemJobStore>());
        builder.Services.AddSingleton<IMethodPolicyStore>(sp => sp.GetRequiredService<FilesystemJobStore>());
        builder.Services.AddSingleton<INodeStore>(sp => sp.GetRequiredService<FilesystemJobStore>());
        builder.Services.AddSingleton<IJobLogStore>(sp => sp.GetRequiredService<FilesystemJobStore>());
        builder.Services.AddSingleton<IJobProgressStore>(sp => sp.GetRequiredService<FilesystemJobStore>());
        builder.Services.AddSingleton<IServerStore>(sp => sp.GetRequiredService<FilesystemJobStore>());
        return builder;
    }
}
