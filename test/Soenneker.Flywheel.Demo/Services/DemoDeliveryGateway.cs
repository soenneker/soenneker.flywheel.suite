using System.Security.Cryptography;
using System.Text;
using Soenneker.Flywheel.Demo.Services.Abstract;
using Soenneker.Flywheel.Redis;
using Soenneker.Redis.Client.Abstract;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Demo.Services;

public sealed class DemoDeliveryGateway(IRedisClient redis, FlywheelRedisOptions options, ILogger<DemoDeliveryGateway> logger) : IDemoDeliveryGateway
{
    public async Task Deliver(string requestId, CancellationToken cancellationToken)
    {
        IDatabase db = (await redis.Get(options.ConnectionString, cancellationToken)).GetDatabase(options.Database);
        string key = "flywheel-demo:delivery:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Namespace + ":" + requestId)));
        var call = (int)await db.ScriptEvaluateAsync("local n=redis.call('INCR',KEYS[1]); redis.call('EXPIRE',KEYS[1],604800); return n",
            [(RedisKey)key]).WaitAsync(cancellationToken);
        logger.LogInformation("Simulated delivery service call {Call} for {RequestId}.", call, requestId);
        if (call <= 2)
        {
            logger.LogWarning("Simulated downstream service is temporarily unavailable. Flywheel will retry.");
            throw new HttpRequestException("Simulated transient delivery failure");
        }
        logger.LogInformation("Delivery simulation accepted. The failure counter survives process restarts.");
    }
}
