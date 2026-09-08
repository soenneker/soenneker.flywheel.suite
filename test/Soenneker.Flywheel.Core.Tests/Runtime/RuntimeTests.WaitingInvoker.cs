using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Core.Tests.Runtime;

public sealed partial class RuntimeTests
{
    private sealed class WaitingInvoker : IJobInvoker
    {
        public string Name => "test";
        public bool Cancelled;
        public async Task Invoke(IServiceProvider services, string payload, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
}
