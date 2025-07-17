using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Log entry for in-memory storage
    /// </summary>
    public record LogEntry(
        DateTime Timestamp,
        LogLevel Level,
        string Category,
        string Message,
        Exception? Exception = null);

    /// <summary>
    /// Interface for in-memory logger sink
    /// </summary>
    public interface IInMemoryLoggerSink : INotifyPropertyChanged
    {
        /// <summary>
        /// Event triggered when new log entry is added
        /// </summary>
        event Action<LogEntry>? LogAdded;
        /// <summary>
        /// Gets or sets a value indicating whether the in-memory logger sink is enabled.
        /// When enabled, log entries are captured and stored in memory; otherwise, logging is disabled.
        /// </summary>
        bool Enabled { get; set; }
        /// <summary>
        /// All log entries
        /// </summary>
        IReadOnlyList<LogEntry> LogEntries { get; }

        /// <summary>
        /// Add a log entry
        /// </summary>
        void AddLog(LogLevel level, string category, string message, Exception? exception = null);

        /// <summary>
        /// Clear all log entries
        /// </summary>
        void Clear();

        /// <summary>
        /// Get logs filtered by level
        /// </summary>
        IEnumerable<LogEntry> GetLogs(LogLevel minimumLevel = LogLevel.Trace);

        /// <summary>
        /// Get recent logs (last N entries)
        /// </summary>
        IEnumerable<LogEntry> GetRecentLogs(int count = 100);
    }
}