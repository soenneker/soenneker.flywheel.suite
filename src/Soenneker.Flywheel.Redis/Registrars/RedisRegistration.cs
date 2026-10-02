using Soenneker.Redis.Client.Abstract;
using Soenneker.Librarian.Redis;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Redis.Client.Registrars;

namespace Soenneker.Flywheel.Redis;

/// <summary>Registers the Redis persistence provider with a Flywheel runtime.</summary>
public static class RedisRegistration
{
    /// <summary>The DI key for Flywheel's Redis storage dependencies.</summary>
    public const string LibrarianServiceKey = "Flywheel.Redis";

    /// <summary>Adds Redis lifecycle persistence using the shared Soenneker connection cache.</summary>
    public static FlywheelBuilder AddRedis(this FlywheelBuilder builder, Action<FlywheelRedisOptions>? configure = null)
    {
        var options = new FlywheelRedisOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString) || string.IsNullOrWhiteSpace(options.Namespace) || options.Database < 0 ||
            options.HistoryRetention < TimeSpan.FromMinutes(5) || options.HistoryRetention > TimeSpan.FromDays(365))
            throw new ArgumentException("Redis connection, namespace and database must be configured.");
        builder.Services.AddRedisClientAsSingleton();
        builder.Services.AddSingleton(options);
        builder.Services.AddKeyedSingleton<RedisLibrarianDatabase>(LibrarianServiceKey, (sp, _) =>
        {
            var storage = sp.GetRequiredService<FlywheelRedisOptions>();
            var client = sp.GetRequiredService<IRedisClient>();
            return new RedisLibrarianDatabase(sp.GetService<FlywheelOptions>()?.GetStorageName(storage.Namespace) ?? storage.Namespace,
                async ct => (await client.Get(storage.ConnectionString, ct).ConfigureAwait(false)).GetDatabase(storage.Database),
                keyPrefix: storage.KeyPrefix);
        });
        builder.Services.AddSingleton(sp => new RedisJobStore(
            sp.GetRequiredKeyedService<RedisLibrarianDatabase>(LibrarianServiceKey),
            sp.GetRequiredService<FlywheelRedisOptions>(), sp.GetService<FlywheelOptions>()));
        builder.Services.AddHostedService<RedisLiveActivityRecorder>();
        builder.Services.AddSingleton<IJobChangeFeed>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<ICronJobStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobChainStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IMethodPolicyStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<INodeStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobLogStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IJobProgressStore>(sp => sp.GetRequiredService<RedisJobStore>());
        builder.Services.AddSingleton<IServerStore>(sp => sp.GetRequiredService<RedisJobStore>());
        return builder;
    }
}
