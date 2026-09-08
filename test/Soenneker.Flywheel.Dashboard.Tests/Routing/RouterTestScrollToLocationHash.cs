using Microsoft.AspNetCore.Components.Routing;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class RouterTestScrollToLocationHash : IScrollToLocationHash
{
    public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask;
}
