namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Result of a backup operation
    /// </summary>
    public record BackupResult(bool Success, string? BackupFilePath, string? Error)
    {
        /// <summary>
        /// Creates a successful backup result
        /// </summary>
        public static BackupResult CreateSuccess(string backupFilePath) => 
            new(true, backupFilePath, null);

        /// <summary>
        /// Creates a failed backup result
        /// </summary>
        public static BackupResult CreateFailure(string error) => 
            new(false, null, error);
    }
}