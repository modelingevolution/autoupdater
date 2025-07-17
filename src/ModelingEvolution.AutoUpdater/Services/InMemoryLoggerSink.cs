using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

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
        private bool _enabled = true;

        public event Action<LogEntry>? LogAdded;

        public IReadOnlyList<LogEntry> LogEntries => _logs;

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}