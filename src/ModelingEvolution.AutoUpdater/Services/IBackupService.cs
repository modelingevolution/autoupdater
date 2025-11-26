using System.Threading.Tasks;
using ModelingEvolution.AutoUpdater.Models;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for handling backup and restore operations during migrations and management
    /// </summary>
    public interface IBackupService
    {
        /// <summary>
        /// Checks if backup.sh script exists in the specified directory
        /// </summary>
        /// <param name="directory">Directory to check for backup.sh</param>
        /// <returns>True if backup.sh exists and is executable</returns>
        Task<bool> BackupScriptExistsAsync(string directory);

        /// <summary>
        /// Executes backup.sh script and returns backup information.
        /// Optionally accepts a version parameter for versioned backups.
        /// </summary>
        /// <param name="directory">Directory containing backup.sh script</param>
        /// <param name="version">Optional version to tag the backup with</param>
        /// <returns>Result of backup operation</returns>
        Task<BackupResult> CreateBackupAsync(string directory, string? version = null);

        /// <summary>
        /// Checks if restore.sh script exists in the specified directory
        /// </summary>
        /// <param name="directory">Directory to check for restore.sh</param>
        /// <returns>True if restore.sh exists and is executable</returns>
        Task<bool> RestoreScriptExistsAsync(string directory);

        /// <summary>
        /// Executes restore.sh script with the specified backup file
        /// </summary>
        /// <param name="directory">Directory containing restore.sh script</param>
        /// <param name="backupFilePathOrFilename">Path or filename of backup file to restore</param>
        /// <returns>Result of restore operation</returns>
        Task<RestoreResult> RestoreBackupAsync(string directory, string backupFilePathOrFilename);

        /// <summary>
        /// Lists all available backups using backup.sh list command
        /// </summary>
        /// <param name="directory">Directory containing backup.sh script</param>
        /// <returns>Result containing list of backups</returns>
        Task<BackupListResult> ListBackupsAsync(string directory);
    }
}