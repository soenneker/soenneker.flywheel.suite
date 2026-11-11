using Microsoft.Extensions.Logging;

namespace Soenneker.Flywheel.Core.Logging;

public sealed partial class JobLogCapture
{
    private sealed class CaptureLogger(string category, LogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Current.Value is not null && minimumLevel != LogLevel.None &&
            logLevel >= minimumLevel && logLevel < LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                string message = formatter(state, exception);
                if (exception is not null) message += Environment.NewLine + exception.ToString();
                Current.Value?.Write(logLevel.ToString(), category, message);
            }
            catch { /* Diagnostic capture must not fail the handler. */ }
        }
    }
}
