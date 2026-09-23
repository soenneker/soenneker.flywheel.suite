using Soenneker.Blazor.Utils.LocalStorage.Abstract;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class RouterTestLocalStorage : ILocalStorageUtil
{
    public ValueTask Initialize(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask<string?> Get(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(key switch
        {
            "flywheel.timezone" => "UTC",
            "flywheel.timezone.enabled" => "true",
            _ => throw new NotSupportedException(key)
        });

    public ValueTask Set(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<T?> Get<T>(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask Set<T>(string key, T value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask Remove(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask Clear(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<bool> ContainsKey(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<string>> GetKeys(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<int> GetLength(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
