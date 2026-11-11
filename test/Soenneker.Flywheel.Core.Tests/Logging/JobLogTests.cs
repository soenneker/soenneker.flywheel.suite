using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Communication.Dtos;
using System.Threading;

namespace Soenneker.Flywheel.Core.Tests.Logging;

public sealed partial class JobLogTests
{
    [Test]
    public async ValueTask ConcurrentJobLoggingIsIsolatedAndBounded(CancellationToken cancellationToken)
    {
        using var capture = new JobLogCapture();
        var store = new LogStore();
        ILogger logger = capture.CreateLogger("Handler");
        await Task.WhenAll(Run("first"), Run("second"));
        logger.LogInformation("outside");
        foreach (string id in new[] { "first", "second" })
        {
            string[] entries = store.Entries.Where(x => x.Id == id).Select(x => x.Message.Message).ToArray();
            if (!entries.Contains(id) || entries.Any(x => x == "outside" || x == (id == "first" ? "second" : "first")))
                throw new Exception("Execution logs crossed contexts");
            if (entries.Any(x => x.Length > 4096)) throw new Exception("Message was not bounded");
        }
        async Task Run(string id)
        {
            await using JobLogCapture.Session session = capture.Begin(Lease(id), store);
            await Task.Yield();
            logger.LogInformation("{Value}", id);
            logger.LogInformation("{Value}", new string('x', 5000));
        }
    }

    [Test]
    public async ValueTask CapturedErrorIncludesExceptionAndInnerStackTrace()
    {
        using var capture = new JobLogCapture();
        var store = new LogStore();
        Exception failure;
        try
        {
            throw new InvalidOperationException("Underlying failure");
        }
        catch (Exception exception)
        {
            failure = new AggregateException("Rebuild failed", exception, new ArgumentException("Second failure"));
        }
        await using (capture.Begin(Lease("exception"), store))
        {
            capture.CreateLogger("Handler").LogError(failure, "Unable to rebuild {NumberId}", "number-1");
        }
        var entry = store.Entries.Single().Message;
        if (entry.Message != "Unable to rebuild number-1" + Environment.NewLine + failure.ToString())
            throw new Exception("Captured error lost its message or exception details");
        if (!entry.Message.Contains(nameof(CapturedErrorIncludesExceptionAndInnerStackTrace)))
            throw new Exception("Captured error lost the inner exception stack trace");
    }

    [Test]
    public async ValueTask CapturePassesExceptionToCustomFormatter()
    {
        using var capture = new JobLogCapture();
        var store = new LogStore();
        var failure = new InvalidOperationException("Failure");
        Exception? received = null;
        await using (capture.Begin(Lease("formatter"), store))
        {
            capture.CreateLogger("Handler").Log(LogLevel.Error, new EventId(1), "Message", failure, (state, exception) =>
            {
                received = exception;
                return state;
            });
        }
        if (!ReferenceEquals(received, failure)) throw new Exception("Formatter did not receive the exception");
        if (store.Entries.Single().Message.Message != "Message" + Environment.NewLine + failure.ToString())
            throw new Exception("Custom formatted log lost exception details");
    }

    [Test]
    public async ValueTask StorageFailureDoesNotEscapeLogging(CancellationToken cancellationToken)
    {
        using var capture = new JobLogCapture();
        await using JobLogCapture.Session session = capture.Begin(Lease("failed"), new LogStore { Fail = true });
        capture.CreateLogger("Handler").LogInformation("Message");
    }

    private static JobLease Lease(string id) => new(new JobRecord { Id = id, Name = "test", Payload = "{}", Policy = new JobPolicy() }, "token", 1);
}
