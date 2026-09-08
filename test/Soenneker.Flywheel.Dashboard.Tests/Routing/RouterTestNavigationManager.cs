using Microsoft.AspNetCore.Components;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class RouterTestNavigationManager : NavigationManager
{
    public RouterTestNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        Uri = ToAbsoluteUri(uri).AbsoluteUri;
        NotifyLocationChanged(isInterceptedLink: false);
    }
}
