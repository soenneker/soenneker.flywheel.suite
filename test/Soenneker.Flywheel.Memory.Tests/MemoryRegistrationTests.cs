using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Memory;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class MemoryRegistrationTests
{
    [Test]
    public async ValueTask KeyedDatabaseIsIsolatedAndOwnedByServiceProvider()
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
            await store.Enqueue(new EnqueueRequest("job", "{}", new(), TimeSpan.Zero, null));
            var jobs = await database.GetContainer("flywheel.jobs");
            if ((await jobs.GetLibrarianItems()).Count == 0)
                throw new InvalidOperationException("The store did not use the keyed database.");
            await store.DisposeAsync();
            await database.GetContainer("still-alive");
        }
        finally
        {
            await provider.DisposeAsync();
        }
        try
        {
            await database.GetContainer("disposed");
            throw new InvalidOperationException("DI did not dispose its database.");
        }
        catch (ObjectDisposedException) { }
    }
}
