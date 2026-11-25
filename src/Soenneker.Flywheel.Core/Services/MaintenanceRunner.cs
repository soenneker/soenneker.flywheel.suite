using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Core.Services;

public sealed class MaintenanceRunner(IJobStore store, INodeStore nodes, IWorkerPool workers, FlywheelOptions options) : IMaintenanceRunner
{
    internal Task Maintain(CancellationToken token) => store.Maintain(100, token);

    internal Task Heartbeat(CancellationToken token) =>
        nodes.Heartbeat(options.NodeId, workers.Pools, options.LeaseDuration, token);

    public Task RunOnce(CancellationToken cancellationToken) =>
        Task.WhenAll(Heartbeat(cancellationToken), Maintain(cancellationToken));
}
