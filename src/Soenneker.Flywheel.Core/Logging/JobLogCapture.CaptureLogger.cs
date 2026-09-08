using Microsoft.Extensions.Logging;

namespace Soenneker.Flywheel.Core.Logging;

public sealed partial class JobLogCapture
{
    private sealed class CaptureLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Current.Value is not null && logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try { Current.Value?.Write(logLevel.ToString(), category, formatter(state, null)); }
            catch { /* Diagnostic capture must not fail the handler. */ }
        }
    }
}
