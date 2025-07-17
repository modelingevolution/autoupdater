using System;

namespace ModelingEvolution.AutoUpdater.Common.Events
{
    /// <summary>
    /// Event raised when an update process starts
    /// </summary>
    public class UpdateStartedEvent
    {
        public UpdateStartedEvent(string applicationName, string currentVersion, string targetVersion)
        {
            ApplicationName = applicationName ?? throw new ArgumentNullException(nameof(applicationName));
            CurrentVersion = currentVersion;
            TargetVersion = targetVersion ?? throw new ArgumentNullException(nameof(targetVersion));
            StartedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Name of the application being updated
        /// </summary>
        public string ApplicationName { get; }

        /// <summary>
        /// Current version before update (null for initial deployment)
        /// </summary>
        public string? CurrentVersion { get; }

        /// <summary>
        /// Target version to update to
        /// </summary>
        public string TargetVersion { get; }

        /// <summary>
        /// When the update started
        /// </summary>
        public DateTime StartedAt { get; }
    }
}