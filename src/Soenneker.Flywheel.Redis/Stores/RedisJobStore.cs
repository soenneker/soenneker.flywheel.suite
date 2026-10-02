using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Redis;
using Soenneker.Redis.Client.Abstract;
using StackExchange.Redis;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Redis;

public sealed class RedisJobStore : LibrarianJobStore
{
    protected override bool UseServerQueries => true;

    [ActivatorUtilitiesConstructor]
    public RedisJobStore(IRedisClient client, FlywheelRedisOptions options, FlywheelOptions? runtimeOptions = null) : this(
        async ct =>
            (await client.Get(options.ConnectionString, ct).ConfigureAwait(false)).GetDatabase(options.Database),
        runtimeOptions?.GetStorageName(options.Namespace) ?? options.Namespace, options.HistoryRetention,
        options.RetainCompletedJobs, options.KeyPrefix, runtimeOptions?.GetStorageOperationTimeout())
    {
    }

    public RedisJobStore(Func<CancellationToken, Task<IDatabase>> database, string storageNamespace,
        TimeSpan? historyRetention = null, bool retainCompletedJobs = true, string keyPrefix = "flywheel",
        TimeSpan? operationTimeout = null) : this(
        new RedisLibrarianDatabase(storageNamespace, ct => new ValueTask<IDatabase>(database(ct)), keyPrefix: keyPrefix),
        historyRetention, retainCompletedJobs, operationTimeout)
    {
    }

    internal RedisJobStore(RedisLibrarianDatabase database, FlywheelRedisOptions options, FlywheelOptions? runtimeOptions)
        : this(database, options.HistoryRetention, options.RetainCompletedJobs, runtimeOptions?.GetStorageOperationTimeout(), ownsDatabase: false) { }

    private RedisJobStore(RedisLibrarianDatabase database, TimeSpan? historyRetention, bool retainCompletedJobs, TimeSpan? operationTimeout,
        bool ownsDatabase = true) : base(database,
        historyRetention, retainCompletedJobs, ownsDatabase: ownsDatabase,
        clock: async ct => (await database.GetServerTime(ct).ConfigureAwait(false)).ToUnixTimeMilliseconds(), operationTimeout: operationTimeout)
    {
    }
}
