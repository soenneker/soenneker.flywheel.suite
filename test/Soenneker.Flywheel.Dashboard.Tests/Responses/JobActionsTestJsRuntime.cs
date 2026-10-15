using Microsoft.JSInterop;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class JobActionsTestJsRuntime : IJSRuntime, IJSObjectReference
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)this : default!);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InvokeAsync<TValue>(identifier, args);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
