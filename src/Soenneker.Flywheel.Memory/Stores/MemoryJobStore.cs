using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Memory;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Memory;

public sealed class MemoryJobStore : LibrarianJobStore
{
    public MemoryJobStore(FlywheelMemoryOptions options, TimeProvider? timeProvider = null, FlywheelOptions? runtimeOptions = null)
        : base(Create(options), options.HistoryRetention, options.RetainCompletedJobs, timeProvider, ownsDatabase: true,
            operationTimeout: runtimeOptions?.GetStorageOperationTimeout()) { }

    internal MemoryJobStore(MemoryLibrarianDatabase database, FlywheelMemoryOptions options,
        TimeProvider? timeProvider, FlywheelOptions? runtimeOptions)
        : base(database, options.HistoryRetention, options.RetainCompletedJobs, timeProvider,
            operationTimeout: runtimeOptions?.GetStorageOperationTimeout()) { }

    private static MemoryLibrarianDatabase Create(FlywheelMemoryOptions options)
    {
        ValidateOptions(options);
        return new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
    }

    internal static void ValidateOptions(FlywheelMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.HistoryRetention < TimeSpan.FromMinutes(5) || options.HistoryRetention > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(options.HistoryRetention));
    }
}
