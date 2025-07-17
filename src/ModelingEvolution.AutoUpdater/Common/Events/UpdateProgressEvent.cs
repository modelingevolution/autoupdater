using System;

namespace ModelingEvolution.AutoUpdater.Common.Events
{
    /// <summary>
    /// Event raised to report progress during an update
    /// </summary>
    public class UpdateProgressEvent
    {
        public UpdateProgressEvent(string applicationName, string operation, int progressPercentage)
        {
            ApplicationName = applicationName ?? throw new ArgumentNullException(nameof(applicationName));
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
            ProgressPercentage = Math.Max(0, Math.Min(100, progressPercentage));
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Name of the application being updated
        /// </summary>
        public string ApplicationName { get; }

        /// <summary>
        /// Current operation being performed
        /// </summary>
        public string Operation { get; }

        /// <summary>
        /// Progress percentage (0-100)
        /// </summary>
        public int ProgressPercentage { get; }

        /// <summary>
        /// When this progress was reported
        /// </summary>
        public DateTime Timestamp { get; }
    }
}