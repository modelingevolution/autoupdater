namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Result of a restore operation
    /// </summary>
    public record RestoreResult(bool Success, string? Error)
    {
        /// <summary>
        /// Creates a successful restore result
        /// </summary>
        public static RestoreResult CreateSuccess() => 
            new(true, null);

        /// <summary>
        /// Creates a failed restore result
        /// </summary>
        public static RestoreResult CreateFailure(string error) => 
            new(false, error);
    }
}