namespace Soenneker.Flywheel.Filesystem;

internal sealed class FilesystemStorageOwnership(string path) : IAsyncDisposable
{
    private FileStream? _ownership;

    internal void Acquire()
    {
        if (_ownership is not null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownership is not null) await _ownership.DisposeAsync().ConfigureAwait(false);
    }
}
