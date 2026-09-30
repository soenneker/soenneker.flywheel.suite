using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Registrars;

namespace Soenneker.Flywheel.Core.Tests.Logging;

public sealed partial class JobLogTests
{
    [Test]
    public async ValueTask DefaultCaptureExcludesDebug()
    {
        using var capture = new JobLogCapture();
        await VerifyLevels(capture, LogLevel.Information);
    }

    [Test]
    [Arguments(LogLevel.Trace)]
    [Arguments(LogLevel.Debug)]
    [Arguments(LogLevel.Information)]
    [Arguments(LogLevel.Warning)]
    [Arguments(LogLevel.Error)]
    [Arguments(LogLevel.Critical)]
    [Arguments(LogLevel.None)]
    public async ValueTask RegisteredCaptureUsesConfiguredMinimum(LogLevel minimum)
    {
        var services = new ServiceCollection();
        services.AddFlywheel(options => options.MinimumJobLogLevel = minimum);
        using ServiceProvider provider = services.BuildServiceProvider();
        var capture = (JobLogCapture)provider.GetRequiredService<ILoggerProvider>();
        await VerifyLevels(capture, minimum);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(7)]
    public void RegistrationRejectsInvalidMinimum(int minimum)
    {
        try
        {
            new ServiceCollection().AddFlywheel(options => options.MinimumJobLogLevel = (LogLevel)minimum);
        }
        catch (ArgumentOutOfRangeException exception) when (exception.ParamName == nameof(FlywheelOptions.MinimumJobLogLevel))
        {
            return;
        }
        throw new Exception("Invalid capture level was accepted");
    }

    private static async ValueTask VerifyLevels(JobLogCapture capture, LogLevel minimum)
    {
        var store = new LogStore();
        ILogger logger = capture.CreateLogger("Handler");
        if (logger.IsEnabled(LogLevel.Critical)) throw new Exception("Capture enabled outside a job");
        await using (capture.Begin(Lease("levels"), store))
        {
            foreach (LogLevel level in Enum.GetValues<LogLevel>())
            {
                bool expected = minimum != LogLevel.None && level >= minimum && level != LogLevel.None;
                if (logger.IsEnabled(level) != expected) throw new Exception($"Unexpected enabled state for {level}");
                logger.Log(level, "{Level}", level);
            }
        }
        logger.LogCritical("outside");
        string[] expectedMessages = Enum.GetValues<LogLevel>()
            .Where(level => minimum != LogLevel.None && level >= minimum && level != LogLevel.None)
            .Select(level => level.ToString()).OrderBy(message => message).ToArray();
        string[] actualMessages = store.Entries.Select(entry => entry.Message.Message).OrderBy(message => message).ToArray();
        if (!actualMessages.SequenceEqual(expectedMessages)) throw new Exception("Captured messages did not match the configured minimum");
    }
}
