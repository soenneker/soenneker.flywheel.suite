using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Postgres.Tests;

public sealed class PostgresJobStoreTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static EnqueueRequest Request(string? key = null) => new("job", "{}", new JobPolicy(), TimeSpan.Zero, key);

    [Test]
    public async Task RegistrationSharesOneStoreWithoutOpeningDatabase()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheel().AddPostgres(o => o.ConnectionString = "Host=localhost;Database=flywheel");
        await using ServiceProvider provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<PostgresJobStore>();
        Type[] contracts = [typeof(IJobStore), typeof(IJobChangeFeed), typeof(ICronJobStore), typeof(IJobChainStore),
            typeof(IMethodPolicyStore), typeof(INodeStore), typeof(IJobLogStore), typeof(IJobProgressStore), typeof(IServerStore)];
        foreach (Type contract in contracts)
            Check(ReferenceEquals(store, provider.GetRequiredService(contract)), $"Separate store for {contract}.");
        Check(provider.GetServices<IHostedService>().OfType<PostgresLiveActivityRecorder>().Count() == 1, "Missing activity recorder.");
    }

    [Test]
    public void InvalidOptionsFailAtRegistrationAndConstruction()
    {
        Action<FlywheelPostgresOptions>[] invalid = [o => o.ConnectionString = " ", o => o.Namespace = " ",
            o => o.ConnectionString = "invalid", o => o.HistoryRetention = TimeSpan.FromMinutes(4),
            o => o.HistoryRetention = TimeSpan.FromDays(366)];
        foreach (Action<FlywheelPostgresOptions> configure in invalid)
        {
            var options = new FlywheelPostgresOptions { ConnectionString = "Host=localhost" };
            configure(options);
            bool rejected = false;
            try { _ = new PostgresJobStore(options); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid constructor configuration accepted.");
            rejected = false;
            try { new ServiceCollection().AddFlywheel().AddPostgres(o => { o.ConnectionString = "Host=localhost"; configure(o); }); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid registration configuration accepted.");
        }
    }

    [Test]
    public async Task JobsLeasesAndHistorySurviveReopening()
    {
        await using var fixture = new DatabaseFixture();
        string id;
        JobLease lease;
        await using (PostgresJobStore store = fixture.Open())
        {
            id = await store.Enqueue(Request("dedupe"));
            lease = (await store.Claim("worker", TimeSpan.FromMinutes(1)))!;
            Check(lease is not null, "Job was not claimed.");
            await store.SetProgress(lease!, 25, "working");
            await store.AppendLogs(lease!, [new JobLogMessage("Info", "test", "persisted")]);
            await store.Heartbeat("worker", 2, TimeSpan.FromMinutes(1));
        }
        await using (PostgresJobStore store = fixture.Open())
        {
            JobRecord job = (await store.Get(id))!;
            Check(job.State == JobState.Running && job.Progress == 25, "Job state did not persist.");
            Check((await store.GetLogs(id)).Single().Message == "persisted", "Logs did not persist.");
            Check(await store.GetTotalWorkerCount() == 2, "Worker heartbeat did not persist.");
            Check(await store.Enqueue(Request("dedupe")) == id, "Dedupe did not persist.");
            Check(await store.Finish(lease!, JobOutcome.Succeeded, null, TimeSpan.Zero), "Lease did not persist.");
            Check((await store.GetSearchHistory(null, null, null)).Sum(p => p.Succeeded) == 1, "Completion history missing.");
        }
    }

    [Test]
    public async Task IndependentWorkersCoordinateClaimsAndNamespaces()
    {
        await using var fixture = new DatabaseFixture();
        await using PostgresJobStore first = fixture.Open();
        await using PostgresJobStore second = fixture.Open();
        string[] ids = await Task.WhenAll(first.Enqueue(Request("same")), second.Enqueue(Request("same")));
        Check(ids[0] == ids[1], "Concurrent dedupe produced two jobs.");
        JobLease?[] leases = await Task.WhenAll(first.Claim("first", TimeSpan.FromMinutes(1)), second.Claim("second", TimeSpan.FromMinutes(1)));
        Check(leases.Count(l => l is not null) == 1, "Workers both claimed the same job.");
        Check(await second.Finish(leases.Single(l => l is not null)!, JobOutcome.Succeeded, null, TimeSpan.Zero), "Cross-instance finish failed.");
        await using var isolatedFixture = new DatabaseFixture();
        await using PostgresJobStore isolated = isolatedFixture.Open();
        Check(await isolated.Get(ids[0]) is null, "Job leaked across namespaces.");
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly string _connection;
        private readonly string _key = $"flywheel-tests-{Guid.NewGuid():N}";

        public DatabaseFixture()
        {
            string? connection = Environment.GetEnvironmentVariable("FLYWHEEL_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(connection)) Skip.Test("Set FLYWHEEL_TEST_POSTGRES to run PostgreSQL integration tests.");
            _connection = connection!;
        }

        public PostgresJobStore Open() => new(new FlywheelPostgresOptions { ConnectionString = _connection, Namespace = _key });

        public async ValueTask DisposeAsync()
        {
            await using NpgsqlDataSource source = NpgsqlDataSource.Create(_connection);
            string encoded = string.Concat(_key.Select(c => ((int)c).ToString("X4")));
            foreach (string table in new[] { "documents", "indexes", "databases" })
            {
                await using NpgsqlCommand command = source.CreateCommand($"DELETE FROM public.librarian_postgres_{table} WHERE database_key=$1");
                command.Parameters.AddWithValue(encoded);
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
