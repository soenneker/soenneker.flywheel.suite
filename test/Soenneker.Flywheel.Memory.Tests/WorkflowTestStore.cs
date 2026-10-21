using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Abstractions;
namespace Soenneker.Flywheel.Memory.Tests;

internal sealed class WorkflowTestStore(ILibrarianDatabase database) : LibrarianJobStore(database);
