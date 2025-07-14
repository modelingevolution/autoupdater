using System;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for tracking and reporting update progress
    /// </summary>
    public interface IProgressService
    {
        /// <summary>
        /// Event triggered when progress changes
        /// </summary>
        event Action? Changed;

        /// <summary>
        /// Current operation being performed
        /// </summary>
        string CurrentOperation { get; }

        /// <summary>
        /// Overall progress percentage (0-100)
        /// </summary>
        int ProgressPercentage { get; }

        /// <summary>
        /// Whether an update operation is currently running
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Current status message
        /// </summary>
        string StatusMessage { get; }

        /// <summary>
        /// Number of packages being updated
        /// </summary>
        int TotalPackages { get; }

        /// <summary>
        /// Number of packages completed
        /// </summary>
        int CompletedPackages { get; }

        /// <summary>
        /// Update the current operation
        /// </summary>
        void UpdateOperation(string operation);

        /// <summary>
        /// Update progress percentage
        /// </summary>
        void UpdateProgress(int percentage);

        /// <summary>
        /// Update status message
        /// </summary>
        void UpdateStatus(string message);

        /// <summary>
        /// Set total number of packages
        /// </summary>
        void SetTotalPackages(int total);

        /// <summary>
        /// Increment completed packages count
        /// </summary>
        void IncrementCompleted();

        /// <summary>
        /// Start tracking an update operation
        /// </summary>
        void StartOperation(string operation, int totalPackages = 1);

        /// <summary>
        /// Complete the current operation
        /// </summary>
        void CompleteOperation();

        /// <summary>
        /// Reset all progress tracking
        /// </summary>
        void Reset();
    }
}