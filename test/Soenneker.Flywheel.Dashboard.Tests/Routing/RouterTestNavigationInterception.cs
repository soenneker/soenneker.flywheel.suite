using Microsoft.AspNetCore.Components.Routing;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class RouterTestNavigationInterception : INavigationInterception
{
    public Task EnableNavigationInterceptionAsync() => Task.CompletedTask;
}
