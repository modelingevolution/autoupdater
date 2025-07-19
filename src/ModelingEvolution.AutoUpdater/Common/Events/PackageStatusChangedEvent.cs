namespace ModelingEvolution.AutoUpdater.Common.Events
{
    /// <summary>
    /// Event fired when a package's Docker Compose status changes
    /// </summary>
    public class PackageStatusChangedEvent
    {
        public PackageName PackageName { get; }
        public string Status { get; }
        public string? PreviousStatus { get; }

        public PackageStatusChangedEvent(PackageName packageName, string status, string? previousStatus = null)
        {
            PackageName = packageName;
            Status = status;
            PreviousStatus = previousStatus;
        }
    }
}