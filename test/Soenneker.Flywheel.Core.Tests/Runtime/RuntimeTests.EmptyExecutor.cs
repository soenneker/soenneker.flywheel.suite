using System.Threading;
using System.Threading.Tasks;
using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Core.Tests.Runtime;

public sealed partial class RuntimeTests
{
    private sealed class EmptyExecutor : IJobExecutor
    {
        public Task<bool> RunOnce(System.Action onClaimed, CancellationToken cancellationToken) => RunOnce(cancellationToken);

        private int _claims;
        public int Claims => Volatile.Read(ref _claims);

        public Task<bool> RunOnce(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _claims);
            return Task.FromResult(false);
        }
    }
}
