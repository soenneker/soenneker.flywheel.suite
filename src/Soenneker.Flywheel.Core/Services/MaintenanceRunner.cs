using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Core.Services;

public sealed class MaintenanceRunner(IJobStore store, INodeStore nodes, IWorkerPool workers, FlywheelOptions options) : IMaintenanceRunner
{
    public async Task RunOnce(CancellationToken cancellationToken)
    {
        await store.Maintain(100, cancellationToken);
        await nodes.Heartbeat(options.NodeId, workers.WorkerCount, options.LeaseDuration, cancellationToken);
    }
}
