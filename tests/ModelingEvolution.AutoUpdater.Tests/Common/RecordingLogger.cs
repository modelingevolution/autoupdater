using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ModelingEvolution.AutoUpdater.Tests.Common
{
    internal sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    /// <summary>Captures log entries so a test can assert on level, exception and message.</summary>
    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }
}
