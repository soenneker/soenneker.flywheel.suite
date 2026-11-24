using Soenneker.Redis.Client.Abstract;
using Soenneker.Librarian.Redis;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Redis.Client.Registrars;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

/// <summary>Registers the Redis persistence provider with a Flywheel runtime.</summary>
public static class RedisRegistration
{
    /// <summary>The DI key for Flywheel's Redis storage dependencies.</summary>
    public const string LibrarianServiceKey = "Flywheel.Redis";

    /// <summary>Adds Redis lifecycle persistence using the shared Soenneker connection cache.</summary>
    public static FlywheelBuilder AddRedis(this FlywheelBuilder builder, Action<FlywheelRedisOptions>? configure = null)
        => AddRedis(builder, configure, connectionFactory: null);

    /// <summary>Adds Redis persistence with an optional factory for a shared, caller-owned connection.</summary>
    /// <param name="builder">The Flywheel builder.</param>
    /// <param name="configure">Configures storage, including the database index and namespace.</param>
    /// <param name="connectionFactory">Resolves a reusable multiplexer from the root service provider. Flywheel passes
    /// the storage cancellation token and leaves disposal to the owner. A null factory uses the configured connection string.</param>
    public static FlywheelBuilder AddRedis(this FlywheelBuilder builder, Action<FlywheelRedisOptions>? configure,
        Func<IServiceProvider, CancellationToken, ValueTask<IConnectionMultiplexer>>? connectionFactory)
    {
        var options = new FlywheelRedisOptions();
        configure?.Invoke(options);
        if ((connectionFactory is null && string.IsNullOrWhiteSpace(options.ConnectionString)) || string.IsNullOrWhiteSpace(options.Namespace) || options.Database < 0 ||
            options.HistoryRetention < TimeSpan.FromMinutes(5) || options.HistoryRetention > TimeSpan.FromDays(365))
            throw new ArgumentException("Redis connection, namespace and database must be configured.");
        if (connectionFactory is null)
            builder.Services.AddRedisClientAsSingleton();
        builder.Services.AddSingleton(options);
        builder.Services.AddKeyedSingleton<RedisLibrarianDatabase>(LibrarianServiceKey, (sp, _) =>
        {
            var storage = sp.GetRequiredService<FlywheelRedisOptions>();
            Func<CancellationToken, ValueTask<IDatabase>> database;
            if (connectionFactory is null)
            {
                var client = sp.GetRequiredService<IRedisClient>();
                database = async ct => (await client.Get(storage.ConnectionString, ct).ConfigureAwait(false)).GetDatabase(storage.Database);
            }
            else
                database = async ct => (await connectionFactory(sp, ct).ConfigureAwait(false)).GetDatabase(storage.Database);

            return new RedisLibrarianDatabase(sp.GetService<FlywheelOptions>()?.GetStorageName(storage.Namespace) ?? storage.Namespace,
                database, keyPrefix: storage.KeyPrefix);
        });
        builder.Services.AddSingleton(sp => new RedisJobStore(
            sp.GetRequiredKeyedService<RedisLibrarianDatabase>(LibrarianServiceKey),
            sp.GetRequiredService<FlywheelRedisOptions>(), sp.GetService<FlywheelOptions>()));
        builder.Services.AddHostedService<RedisLiveActivityRecorder>();
        builder.Services.AddSingleton<IJobChangeFeed>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<ICronJobStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobChainStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobWorkflowStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobDefinitionStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IMethodPolicyStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<INodeStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobLogStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobProgressStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IServerStore>(sp => sp.GetRequiredService<RedisJobStore>());
        return builder;
    }
}
