using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Implementation of progress tracking service
    /// </summary>
    public class ProgressService : IProgressService
    {
        private readonly object _lock = new();
        private readonly IEventHub _eventHub;
        private readonly ILogger<ProgressService> _logger;

        public ProgressService(IEventHub eventHub, ILogger<ProgressService> logger)
        {
            _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public event Action? Changed;

        public string CurrentOperation { get; private set; } = string.Empty;
        public string CurrentApplication { get; private set; } = string.Empty;
        public int ProgressPercentage { get; private set; }
        public bool IsRunning { get; private set; }
        public string StatusMessage { get; private set; } = string.Empty;
        public int TotalPackages { get; private set; }
        public int CompletedPackages { get; private set; }
        public float? PhaseProgress { get; private set; }
        public string? PhaseMessage { get; private set; }

        public void UpdateOperation(string operation)
        {
            lock (_lock)
            {
                CurrentOperation = operation;
                ClearPhase();
                NotifyChanged();
                PublishProgressEvent();
            }
        }

        public void UpdateProgress(int percentage)
        {
            lock (_lock)
            {
                ProgressPercentage = Math.Clamp(percentage, 0, 100);
                NotifyChanged();
                PublishProgressEvent();
            }
        }

        public void UpdateStatus(string message)
        {
            lock (_lock)
            {
                StatusMessage = message;
                NotifyChanged();
            }
        }

        public void SetTotalPackages(int total)
        {
            lock (_lock)
            {
                TotalPackages = total;
                NotifyChanged();
            }
        }

        public void IncrementCompleted()
        {
            lock (_lock)
            {
                CompletedPackages++;
                if (TotalPackages > 0)
                {
                    ProgressPercentage = (CompletedPackages * 100) / TotalPackages;
                }
                NotifyChanged();
            }
        }

        public void StartOperation(string operation, int totalPackages = 1)
        {
            StartOperation(operation, string.Empty, totalPackages);
        }

        public void StartOperation(string operation, string applicationName, int totalPackages = 1)
        {
            lock (_lock)
            {
                IsRunning = true;
                CurrentOperation = operation;
                CurrentApplication = applicationName;
                TotalPackages = totalPackages;
                CompletedPackages = 0;
                ProgressPercentage = 0;
                ClearPhase();
                StatusMessage = "Starting operation...";
                NotifyChanged();
                PublishProgressEvent();
            }
        }

        public void CompleteOperation()
        {
            lock (_lock)
            {
                IsRunning = false;
                ProgressPercentage = 100;
                ClearPhase();
                CurrentOperation = "Completed";
                StatusMessage = "Operation completed successfully";
                NotifyChanged();
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                IsRunning = false;
                CurrentOperation = string.Empty;
                ClearPhase();
                ProgressPercentage = 0;
                StatusMessage = string.Empty;
                TotalPackages = 0;
                CompletedPackages = 0;
                NotifyChanged();
            }
        }

        private void NotifyChanged()
        {
            Changed?.Invoke();
        }

        public void LogOperationProgress(string message, float? percentage = null, string? logMessage = null, params object[] args)
        {
            lock (_lock)
            {
                CurrentOperation = message;
                
                ClearPhase();
                if (percentage.HasValue)
                {
                    ProgressPercentage = Math.Clamp((int)percentage.Value, 0, 100);
                }
                
                NotifyChanged();
                PublishProgressEvent();
                
                if (!string.IsNullOrEmpty(logMessage))
                {
                    if (args?.Length > 0)
                    {
                        _logger.LogInformation(logMessage, args);
                    }
                    else
                    {
                        _logger.LogInformation("{LogMessage}", logMessage);
                    }
                }
            }
        }

        public void LogPhaseProgress(string operation, float percentage, float? phasePercentage, string phaseMessage)
        {
            lock (_lock)
            {
                CurrentOperation = operation;
                ProgressPercentage = Math.Clamp((int)percentage, 0, 100);
                PhaseProgress = phasePercentage.HasValue ? Math.Clamp(phasePercentage.Value, 0f, 100f) : null;
                PhaseMessage = phaseMessage;
                NotifyChanged();
                PublishProgressEvent();
            }
        }

        private void ClearPhase()
        {
            PhaseProgress = null;
            PhaseMessage = null;
        }

        private void PublishProgressEvent()
        {
            if (!string.IsNullOrEmpty(CurrentApplication) && !string.IsNullOrEmpty(CurrentOperation))
            {
                try
                {
                    _ = Task.Run(async () =>
                    {
                        await _eventHub.PublishAsync(new UpdateProgressEvent(
                            CurrentApplication,
                            CurrentOperation,
                            ProgressPercentage));
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish progress event");
                }
            }
        }
    }
}