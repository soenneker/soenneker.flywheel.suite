using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Memory;
using System.Threading;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class MemoryRegistrationTests
{
    [Test]
    public async ValueTask KeyedDatabaseIsIsolatedAndOwnedByServiceProvider(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILibrarianDatabase>(_ => new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance));
        services.AddFlywheel().AddMemory();
        ServiceProvider provider = services.BuildServiceProvider();
        MemoryLibrarianDatabase database;
        try
        {
            database = provider.GetRequiredKeyedService<MemoryLibrarianDatabase>(MemoryRegistration.LibrarianServiceKey);
            if (ReferenceEquals(database, provider.GetRequiredService<ILibrarianDatabase>()) ||
                provider.GetService<MemoryLibrarianDatabase>() is not null)
                throw new InvalidOperationException("Flywheel's database leaked into unkeyed services.");
            var store = provider.GetRequiredService<MemoryJobStore>();
            await store.Enqueue(new EnqueueRequest("job", "{}", new(), TimeSpan.Zero, null), cancellationToken: cancellationToken);
            var jobs = await database.GetContainer("flywheel.jobs", cancellationToken: cancellationToken);
            if ((await jobs.GetLibrarianItems(cancellationToken: cancellationToken)).Count == 0)
                throw new InvalidOperationException("The store did not use the keyed database.");
            await store.DisposeAsync();
            await database.GetContainer("still-alive", cancellationToken: cancellationToken);
        }
        finally
        {
            await provider.DisposeAsync();
        }
        try
        {
            await database.GetContainer("disposed", cancellationToken: cancellationToken);
            throw new InvalidOperationException("DI did not dispose its database.");
        }
        catch (ObjectDisposedException) { }
    }
}
