using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal interface ILibrarianTable
{
    string Name { get; }
    int WriteCount { get; }
    void AddWrites(List<LibrarianWrite> writes);
    bool Touched { get; }
    /// <summary>Validates point reads by document; indexed scans additionally fence against table mutations.</summary>
    void AddConditions(List<LibrarianCondition> conditions, string controlContainer, string? tableRevision);
    void Bind(ILibrarianContainer container, CancellationToken token);
    void Reset();
}
