using Microsoft.JSInterop;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class RouterTestJsRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        throw new InvalidOperationException("Static routing must not require JavaScript execution.");

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        throw new InvalidOperationException("Static routing must not require JavaScript execution.");
}
