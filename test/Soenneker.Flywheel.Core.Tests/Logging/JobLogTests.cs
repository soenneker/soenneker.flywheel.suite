using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Core.Dtos;

namespace Soenneker.Flywheel.Core.Tests.Logging;

public sealed partial class JobLogTests
{
    [Test]
    public async Task ConcurrentJobLoggingIsIsolatedAndBounded()
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
    public async Task StorageFailureDoesNotEscapeLogging()
    {
        using var capture = new JobLogCapture();
        await using JobLogCapture.Session session = capture.Begin(Lease("failed"), new LogStore { Fail = true });
        capture.CreateLogger("Handler").LogInformation("Message");
    }

    private static JobLease Lease(string id) => new(new JobRecord { Id = id, Name = "test", Payload = "{}", Policy = new JobPolicy() }, "token", 1);
}
