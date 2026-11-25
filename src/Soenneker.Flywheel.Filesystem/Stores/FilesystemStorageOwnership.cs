namespace Soenneker.Flywheel.Filesystem;

internal sealed class FilesystemStorageOwnership(string path) : IAsyncDisposable
{
    private FileStream? _ownership;

    private readonly Lock _gate = new();
    private bool _disposed;

    internal void Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ownership is not null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            return _ownership?.DisposeAsync() ?? ValueTask.CompletedTask;
        }
    }
}
