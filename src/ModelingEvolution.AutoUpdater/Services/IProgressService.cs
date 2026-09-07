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
        /// Current application being updated
        /// </summary>
        string CurrentApplication { get; }

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
        /// Completion of the sub-phase inside the current operation (0-100), or null when the sub-phase
        /// has no measurable progress yet. Meaningful only while <see cref="PhaseMessage"/> is set.
        /// </summary>
        float? PhaseProgress { get; }

        /// <summary>
        /// Caption of the sub-phase inside the current operation, or null when no sub-phase is running.
        /// </summary>
        string? PhaseMessage { get; }

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
        /// Start tracking an update operation for a specific application
        /// </summary>
        void StartOperation(string operation, string applicationName, int totalPackages = 1);

        /// <summary>
        /// Complete the current operation
        /// </summary>
        void CompleteOperation();

        /// <summary>
        /// Reset all progress tracking
        /// </summary>
        void Reset();

        /// <summary>
        /// Log operation progress with optional percentage update and structured logging
        /// </summary>
        /// <param name="message">The operation message</param>
        /// <param name="percentage">Optional progress percentage (0-100)</param>
        /// <param name="logMessage">Optional log message template</param>
        /// <param name="args">Optional log message arguments for structured logging</param>
        void LogOperationProgress(string message, float? percentage = null, string? logMessage = null, params object[] args);

        /// <summary>
        /// Reports progress of a sub-phase inside the current operation. Updates the operation, the overall
        /// percentage and the sub-phase bar in one change notification. Any other operation update clears the sub-phase.
        /// </summary>
        /// <param name="operation">The operation message</param>
        /// <param name="percentage">Overall progress percentage (0-100)</param>
        /// <param name="phasePercentage">Sub-phase completion (0-100), or null for indeterminate</param>
        /// <param name="phaseMessage">Sub-phase caption</param>
        void LogPhaseProgress(string operation, float percentage, float? phasePercentage, string phaseMessage);
    }
}