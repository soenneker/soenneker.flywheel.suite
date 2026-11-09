using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Flywheel.Memory.Tests;

internal sealed class RecurringRecoveryStore(ILibrarianDatabase database, TimeProvider? timeProvider = null)
    : LibrarianJobStore(database, retainCompletedJobs: false, timeProvider: timeProvider);
