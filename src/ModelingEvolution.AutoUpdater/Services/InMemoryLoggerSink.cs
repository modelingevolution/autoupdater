using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// In-memory logger sink implementation
    /// </summary>
    public class InMemoryLoggerSink : IInMemoryLoggerSink
    {
        private readonly ObservableCollection<LogEntry> _logs = new();
        private readonly int _maxEntries;
        private volatile LogEntry? _lastEntry;

        public event Action<LogEntry>? LogAdded;

        public IReadOnlyList<LogEntry> LogEntries => _logs;
        public bool Enabled { get; set; } = true;
        public InMemoryLoggerSink(int maxEntries = 1000)
        {
            _maxEntries = maxEntries;
        }

        public void AddLog(LogLevel level, string category, string message, Exception? exception = null)
        {
            if (!Enabled)
                return;
            var entry = new LogEntry(DateTime.Now, level, category, message, exception);

            var le = _lastEntry;
            // Do not enqueue if last message content is the same (prevent spam)
            if (le != null && 
                le.Level == level && 
                le.Category == category && 
                le.Message == message &&
                Equals(le.Exception, exception))
            {
                return; // Skip duplicate message
            }

            _logs.Add(entry);
            _lastEntry = entry;
            
            // Maintain max entries limit
            while (_logs.Count > _maxEntries)
            {
                _logs.RemoveAt(0);
            }

        }

        public void Clear()
        {
            _logs.Clear();
            _lastEntry = null;
        }

        public IEnumerable<LogEntry> GetLogs(LogLevel minimumLevel = LogLevel.Trace)
        {
            return _logs.Where(log => log.Level >= minimumLevel);
        }

        public IEnumerable<LogEntry> GetRecentLogs(int count = 100)
        {
            return _logs.TakeLast(count);
        }
    }
}