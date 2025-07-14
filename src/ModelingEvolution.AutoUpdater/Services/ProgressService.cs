using System;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Implementation of progress tracking service
    /// </summary>
    public class ProgressService : IProgressService
    {
        private readonly object _lock = new();

        public event Action? Changed;

        public string CurrentOperation { get; private set; } = string.Empty;
        public int ProgressPercentage { get; private set; }
        public bool IsRunning { get; private set; }
        public string StatusMessage { get; private set; } = string.Empty;
        public int TotalPackages { get; private set; }
        public int CompletedPackages { get; private set; }

        public void UpdateOperation(string operation)
        {
            lock (_lock)
            {
                CurrentOperation = operation;
                NotifyChanged();
            }
        }

        public void UpdateProgress(int percentage)
        {
            lock (_lock)
            {
                ProgressPercentage = Math.Clamp(percentage, 0, 100);
                NotifyChanged();
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
            lock (_lock)
            {
                IsRunning = true;
                CurrentOperation = operation;
                TotalPackages = totalPackages;
                CompletedPackages = 0;
                ProgressPercentage = 0;
                StatusMessage = "Starting operation...";
                NotifyChanged();
            }
        }

        public void CompleteOperation()
        {
            lock (_lock)
            {
                IsRunning = false;
                ProgressPercentage = 100;
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
    }
}