using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NetDigger.App;

/// <summary>Collects engine log lines into a queue the UI drains on a timer.</summary>
internal sealed class UiLogger(ConcurrentQueue<string> lines) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var tag = logLevel switch
        {
            LogLevel.Warning => "warn ",
            LogLevel.Error or LogLevel.Critical => "error",
            _ => "info "
        };
        lines.Enqueue($"{DateTime.Now:HH:mm:ss}  {tag}  {formatter(state, exception)}");
    }
}
