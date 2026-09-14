using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Core.Tests.Runtime;

public sealed partial class RuntimeTests
{
    private sealed class CancelledDependencyInvoker : IJobInvoker
    {
        public string Name => "test";
        public Task Invoke(IServiceProvider services, string payload, CancellationToken cancellationToken) =>
            Task.FromException(new OperationCanceledException("Dependency-specific cancellation"));
    }
}
