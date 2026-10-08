using System.Threading.Tasks;
using Soenneker.Flywheel.Redis.Performance;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests.Benchmarks;

public sealed class BenchmarkRunner
{
    [Test]
    [Explicit]
    [NotInParallel]
    public ValueTask RedisOperations(CancellationToken cancellationToken) => new ValueTask(RedisOperationsBenchmark.Run());
}
