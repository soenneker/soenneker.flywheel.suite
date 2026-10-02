using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.FileSystem;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Filesystem;

public sealed class FilesystemJobStore : LibrarianJobStore
{
    private readonly FilesystemStorageOwnership _ownership;
    private readonly bool _ownsStorageOwnership;

    public FilesystemJobStore(FlywheelFilesystemOptions options, IFileUtil fileUtil, IMemoryStreamUtil memoryStreamUtil,
        ILogger<FilesystemJobStore> logger, TimeProvider? timeProvider = null, FlywheelOptions? runtimeOptions = null)
        : base(Create(options, fileUtil, memoryStreamUtil, logger, runtimeOptions), options.HistoryRetention, options.RetainCompletedJobs,
            timeProvider, ownsDatabase: true, operationTimeout: runtimeOptions?.GetStorageOperationTimeout())
    {
        _ownership = new FilesystemStorageOwnership(Path.GetFullPath(runtimeOptions?.GetStorageName(options.FilePath) ?? options.FilePath));
        _ownsStorageOwnership = true;
    }

    internal FilesystemJobStore(FileSystemLibrarianDatabase database, FilesystemStorageOwnership ownership, FlywheelFilesystemOptions options,
        TimeProvider? timeProvider, FlywheelOptions? runtimeOptions)
        : base(database, options.HistoryRetention, options.RetainCompletedJobs, timeProvider,
            operationTimeout: runtimeOptions?.GetStorageOperationTimeout()) =>
        _ownership = ownership;

    private static FileSystemLibrarianDatabase Create(FlywheelFilesystemOptions options, IFileUtil file,
        IMemoryStreamUtil streams, ILogger logger, FlywheelOptions? runtimeOptions)
    {
        ValidateOptions(options);
        return new FileSystemLibrarianDatabase(runtimeOptions?.GetStorageName(options.FilePath) ?? options.FilePath, file, streams, logger);
    }

    internal static void ValidateOptions(FlywheelFilesystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FilePath);
        if (options.HistoryRetention < TimeSpan.FromMinutes(5) || options.HistoryRetention > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(options.HistoryRetention));
    }

    protected override ValueTask BeforeOperation(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ownership.Acquire();
        return ValueTask.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync().ConfigureAwait(false); }
        finally { if (_ownsStorageOwnership) await _ownership.DisposeAsync().ConfigureAwait(false); }
    }
}
