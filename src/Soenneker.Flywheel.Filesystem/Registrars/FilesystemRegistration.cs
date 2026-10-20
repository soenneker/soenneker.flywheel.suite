using Microsoft.Extensions.Logging;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;
using Soenneker.Librarian.FileSystem;
using Soenneker.Flywheel.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.MemoryStream.Registrars;

using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Filesystem;

/// <summary>Registers filesystem-backed Flywheel storage using Librarian documents.</summary>
public static class FilesystemRegistration
{
    /// <summary>The DI key for Flywheel's Filesystem storage dependencies.</summary>
    public const string LibrarianServiceKey = "Flywheel.Filesystem";

    /// <summary>Adds a filesystem database owned exclusively by this runtime.</summary>
    public static FlywheelBuilder AddFilesystem(this FlywheelBuilder builder, Action<FlywheelFilesystemOptions>? configure = null)
    {
        var options = new FlywheelFilesystemOptions();
        configure?.Invoke(options);
        FilesystemJobStore.ValidateOptions(options);
        builder.Services.AddFileUtilAsSingleton().AddMemoryStreamUtilAsSingleton();
        builder.Services.AddSingleton(options);
        builder.Services.AddKeyedSingleton<FilesystemStorageOwnership>(LibrarianServiceKey, (sp, _) =>
        {
            var storage = sp.GetRequiredService<FlywheelFilesystemOptions>();
            return new FilesystemStorageOwnership(Path.GetFullPath(sp.GetService<FlywheelOptions>()?.GetStorageName(storage.FilePath) ?? storage.FilePath));
        });
        builder.Services.AddKeyedSingleton<FileSystemLibrarianDatabase>(LibrarianServiceKey, (sp, _) =>
        {
            var storage = sp.GetRequiredService<FlywheelFilesystemOptions>();
            // Resolve ownership first so DI releases the file lock after the database has flushed and disposed.
            _ = sp.GetRequiredKeyedService<FilesystemStorageOwnership>(LibrarianServiceKey);
            return new FileSystemLibrarianDatabase(sp.GetService<FlywheelOptions>()?.GetStorageName(storage.FilePath) ?? storage.FilePath,
                sp.GetRequiredService<IFileUtil>(), sp.GetRequiredService<IMemoryStreamUtil>(), sp.GetRequiredService<ILogger<FilesystemJobStore>>());
        });
        builder.Services.AddSingleton(sp => new FilesystemJobStore(
            sp.GetRequiredKeyedService<FileSystemLibrarianDatabase>(LibrarianServiceKey),
            sp.GetRequiredKeyedService<FilesystemStorageOwnership>(LibrarianServiceKey),
            sp.GetRequiredService<FlywheelFilesystemOptions>(), sp.GetService<TimeProvider>(), sp.GetService<FlywheelOptions>()));
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
