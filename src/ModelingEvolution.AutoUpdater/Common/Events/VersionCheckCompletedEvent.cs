namespace ModelingEvolution.AutoUpdater.Common.Events
{
    /// <summary>
    /// Event fired when version check is completed for an application
    /// </summary>
    public class VersionCheckCompletedEvent
    {
        public string ApplicationName { get; }
        public string CurrentVersion { get; }
        public string? AvailableVersion { get; }
        public bool IsUpgradeAvailable { get; }
        public string? ErrorMessage { get; }

        public VersionCheckCompletedEvent(string applicationName, string currentVersion, string? availableVersion, bool isUpgradeAvailable, string? errorMessage = null)
        {
            ApplicationName = applicationName;
            CurrentVersion = currentVersion;
            AvailableVersion = availableVersion;
            IsUpgradeAvailable = isUpgradeAvailable;
            ErrorMessage = errorMessage;
        }
    }
}