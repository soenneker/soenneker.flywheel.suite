using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Flywheel.Memory.Tests;

internal sealed class DebounceSharedStore(ILibrarianDatabase database) : LibrarianJobStore(database);
