using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal interface ILibrarianTable
{
    string Name { get; }
    IEnumerable<LibrarianWrite> Writes { get; }
    bool Touched { get; }
    void Bind(ILibrarianContainer container, CancellationToken token);
    void Reset();
}
