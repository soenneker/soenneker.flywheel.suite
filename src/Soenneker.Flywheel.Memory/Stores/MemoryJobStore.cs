using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Memory;

namespace Soenneker.Flywheel.Memory;

public sealed class MemoryJobStore : LibrarianJobStore
{
    public MemoryJobStore(FlywheelMemoryOptions options, TimeProvider? timeProvider = null)
        : base(Create(options), options.HistoryRetention, options.RetainCompletedJobs, timeProvider, ownsDatabase: true) { }

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
