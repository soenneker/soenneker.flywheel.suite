using System.Reflection;
using System.Runtime.ExceptionServices;
using Soenneker.Utils.File.Abstract;

namespace Soenneker.Flywheel.Filesystem.Tests;

public class FailingFiles : DispatchProxy
{
    public IFileUtil Inner { get; set; } = null!;
    public bool FailWrites { get; set; }
    public bool PartialWrite { get; set; }
    public bool FailReads { get; set; }
    public int RejectedWrites { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method!.Name == "WriteAtomically")
        {
            if (FailWrites) { RejectedWrites++; throw new IOException("Injected Librarian write failure."); }
            if (PartialWrite && arguments![1] is Func<Stream, CancellationToken, ValueTask>)
                arguments[1] = (Func<Stream, CancellationToken, ValueTask>)(async (stream, token) =>
                {
                    await stream.WriteAsync(new byte[] { 123 }, token);
                    throw new IOException("Injected partial write failure.");
                });
        }
        if (FailReads && method.Name == "OpenRead") throw new IOException("Injected Librarian load failure.");
        try { return method.Invoke(Inner, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
