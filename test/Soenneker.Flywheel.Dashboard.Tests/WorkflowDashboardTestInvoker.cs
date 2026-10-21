using Soenneker.Flywheel.Core.Services.Abstract;
namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class WorkflowDashboardTestInvoker : IJobInvoker
{
    public string Name => "job";
    public Task Invoke(IServiceProvider services, string payload, CancellationToken cancellationToken) => Task.CompletedTask;
}
