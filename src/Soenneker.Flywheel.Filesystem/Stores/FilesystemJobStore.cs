using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.FileSystem;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;

namespace Soenneker.Flywheel.Filesystem;

public sealed class FilesystemJobStore : LibrarianJobStore
{
    private readonly string _path;
    private FileStream? _ownership;

    public FilesystemJobStore(FlywheelFilesystemOptions options, IFileUtil fileUtil, IMemoryStreamUtil memoryStreamUtil,
        ILogger<FilesystemJobStore> logger, TimeProvider? timeProvider = null)
        : base(Create(options, fileUtil, memoryStreamUtil, logger), options.HistoryRetention, options.RetainCompletedJobs,
            timeProvider, ownsDatabase: true) => _path = Path.GetFullPath(options.FilePath);

    private static FileSystemLibrarianDatabase Create(FlywheelFilesystemOptions options, IFileUtil file,
        IMemoryStreamUtil streams, ILogger logger)
    {
        ValidateOptions(options);
        return new FileSystemLibrarianDatabase(options.FilePath, file, streams, logger);
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
        if (_ownership is not null) return ValueTask.CompletedTask;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _ownership = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return ValueTask.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync().ConfigureAwait(false); }
        finally { if (_ownership is not null) await _ownership.DisposeAsync().ConfigureAwait(false); }
    }
}
