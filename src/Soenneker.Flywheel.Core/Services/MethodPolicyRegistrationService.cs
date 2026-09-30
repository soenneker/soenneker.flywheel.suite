using Microsoft.Extensions.Hosting;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Services;

public sealed class MethodPolicyRegistrationService(IJobStore store, IEnumerable<IJobInvoker> invokers) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (store is not IWorkerJobStore)
            throw new NotSupportedException("Flywheel workers require an IWorkerJobStore implementation.");
        IMethodPolicyStore? policies = store as IMethodPolicyStore;
        foreach (IJobInvoker invoker in invokers)
        {
            if (invoker.Policy is not { } policy) continue;
            if (policies is null)
                throw new NotSupportedException("The job store does not support declared method policies.");
            policy.Validate();
            await policies.ConfigureMethod(invoker.Name, policy, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
