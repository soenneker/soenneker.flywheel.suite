using Soenneker.Asyncs.Locks;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed class StoreLane
{
    internal readonly AsyncLock Gate = new();
    internal readonly LibrarianSession State = new();
    internal int Pending;
}
