using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Redis;
using Soenneker.Redis.Client.Abstract;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed class RedisJobStore : LibrarianJobStore
{
    [ActivatorUtilitiesConstructor]
    public RedisJobStore(IRedisClient client, FlywheelRedisOptions options) : this(
        async ct =>
            (await client.Get(options.ConnectionString, ct).ConfigureAwait(false)).GetDatabase(options.Database),
        options.Namespace, options.HistoryRetention, options.RetainCompletedJobs, options.KeyPrefix)
    {
    }

    public RedisJobStore(Func<CancellationToken, Task<IDatabase>> database, string storageNamespace,
        TimeSpan? historyRetention = null, bool retainCompletedJobs = true, string keyPrefix = "flywheel") : this(
        new RedisLibrarianDatabase(storageNamespace, ct => new ValueTask<IDatabase>(database(ct)), keyPrefix: keyPrefix),
        historyRetention, retainCompletedJobs)
    {
    }

    private RedisJobStore(RedisLibrarianDatabase database, TimeSpan? historyRetention, bool retainCompletedJobs) : base(database,
        historyRetention, retainCompletedJobs, ownsDatabase: true,
        clock: async ct => (await database.GetServerTime(ct).ConfigureAwait(false)).ToUnixTimeMilliseconds())
    {
    }
}
