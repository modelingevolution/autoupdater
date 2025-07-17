using System;
using System.Collections.Generic;

namespace ModelingEvolution.AutoUpdater.Common.Events
{
    /// <summary>
    /// Event raised when an update process completes
    /// </summary>
    public class UpdateCompletedEvent
    {
        public UpdateCompletedEvent(
            string applicationName, 
            string? previousVersion, 
            string newVersion, 
            bool success, 
            string? errorMessage = null,
            IReadOnlyList<string>? executedScripts = null)
        {
            ApplicationName = applicationName ?? throw new ArgumentNullException(nameof(applicationName));
            PreviousVersion = previousVersion;
            NewVersion = newVersion ?? throw new ArgumentNullException(nameof(newVersion));
            Success = success;
            ErrorMessage = errorMessage;
            ExecutedScripts = executedScripts ?? new List<string>();
            CompletedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Name of the application that was updated
        /// </summary>
        public string ApplicationName { get; }

        /// <summary>
        /// Previous version before update (null for initial deployment)
        /// </summary>
        public string? PreviousVersion { get; }

        /// <summary>
        /// New version after update
        /// </summary>
        public string NewVersion { get; }

        /// <summary>
        /// Whether the update was successful
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Error message if the update failed
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// List of migration scripts that were executed
        /// </summary>
        public IReadOnlyList<string> ExecutedScripts { get; }

        /// <summary>
        /// When the update completed
        /// </summary>
        public DateTime CompletedAt { get; }
    }
}