using System.Threading.Tasks;
using ModelingEvolution.AutoUpdater.Models;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for handling backup and restore operations during migrations
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
        /// Executes backup.sh script and returns backup information
        /// </summary>
        /// <param name="directory">Directory containing backup.sh script</param>
        /// <returns>Result of backup operation</returns>
        Task<BackupResult> CreateBackupAsync(string directory);

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
        /// <param name="backupFilePath">Path to backup file to restore</param>
        /// <returns>Result of restore operation</returns>
        Task<RestoreResult> RestoreBackupAsync(string directory, string backupFilePath);
    }
}