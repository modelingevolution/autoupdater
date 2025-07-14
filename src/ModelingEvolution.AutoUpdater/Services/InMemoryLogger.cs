using Microsoft.Extensions.Logging;
using System;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Logger implementation that writes to in-memory sink
    /// </summary>
    public class InMemoryLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly IInMemoryLoggerSink _sink;

        public InMemoryLogger(string categoryName, IInMemoryLoggerSink sink)
        {
            _categoryName = categoryName;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null; // Scopes not supported for simplicity
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Trace;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            _sink.AddLog(logLevel, _categoryName, message, exception);
        }
    }

    /// <summary>
    /// Logger provider for in-memory logging
    /// </summary>
    public class InMemoryLoggerProvider : ILoggerProvider
    {
        private readonly IInMemoryLoggerSink _sink;

        public InMemoryLoggerProvider(IInMemoryLoggerSink sink)
        {
            _sink = sink;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new InMemoryLogger(categoryName, _sink);
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }
}