using System.Threading.Tasks;
using Soenneker.Flywheel.Redis.Performance;

namespace Soenneker.Flywheel.Redis.Tests.Benchmarks;

public sealed class BenchmarkRunner
{
    [Test]
    [Explicit]
    [NotInParallel]
    public Task RedisOperations() => RedisOperationsBenchmark.Run();
}
