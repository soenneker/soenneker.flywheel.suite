using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Core;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class TestPreferenceDatabase : ILibrarianDatabase
{
    private readonly LibrarianContainer _container;
    private List<IdValuePair> _saved;

    public TestPreferenceDatabase() : this([new() { Id = "flywheel.timezone", Value = "\"UTC\"" }]) { }

    private TestPreferenceDatabase(List<IdValuePair> saved)
    {
        _saved = saved;
        _container = new LibrarianContainer(DashboardPreferenceStorage.ContainerName, this, NullLogger.Instance, saved);
    }

    public bool Unavailable { get; init; }
    public bool SaveUnavailable { get; init; }
    public TestPreferenceDatabase Reopen() => new(_saved);

    public ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        if (Unavailable) throw new JSException("Storage blocked");
        if (containerName != DashboardPreferenceStorage.ContainerName) throw new InvalidOperationException("Unexpected container.");
        return ValueTask.FromResult<ILibrarianContainer>(_container);
    }

    public async ValueTask Save(CancellationToken cancellationToken = default)
    {
        if (SaveUnavailable) throw new JSException("Storage write blocked");
        _saved = await _container.GetLibrarianItems(cancellationToken);
    }

    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync()
    {
        _container.Dispose();
        return ValueTask.CompletedTask;
    }
}
