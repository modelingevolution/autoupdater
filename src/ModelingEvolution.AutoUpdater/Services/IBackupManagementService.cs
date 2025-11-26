using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for managing backups including creation, listing, and restoration
    /// </summary>
    public interface IBackupManagementService
    {
        /// <summary>
        /// Lists all available backups for a package
        /// </summary>
        Task<BackupListResponse> ListBackupsAsync(PackageName packageName);

        /// <summary>
        /// Creates a new backup for a package with optional version metadata
        /// </summary>
        /// <param name="packageName">The package to backup</param>
        /// <param name="version">Optional version tag. If null, will auto-detect from deployment state</param>
        Task<BackupCreateResponse> CreateBackupAsync(PackageName packageName, string? version = null);

        /// <summary>
        /// Restores a package from a backup file
        /// </summary>
        Task<BackupRestoreResponse> RestoreBackupAsync(PackageName packageName, string filename);

        /// <summary>
        /// Gets backup system status for a package
        /// </summary>
        Task<BackupStatusResponse> GetBackupStatusAsync(PackageName packageName);
    }

    /// <summary>
    /// Information about a single backup
    /// </summary>
    public record BackupInfo(
        string Filename,
        string DisplayName,
        string Version,
        bool GitTagExists,
        string Size,
        long SizeBytes,
        DateTime CreatedDate,
        string FullPath
    );

    /// <summary>
    /// Response containing list of backups
    /// </summary>
    public record BackupListResponse(
        List<BackupInfo> Backups,
        int TotalCount,
        long TotalSizeBytes,
        string TotalSize,
        string? Error = null
    );

    /// <summary>
    /// Response from backup creation
    /// </summary>
    public record BackupCreateResponse(
        bool Success,
        BackupInfo? Backup = null,
        string? Error = null,
        string? Message = null
    );

    /// <summary>
    /// Response from backup restoration
    /// </summary>
    public record BackupRestoreResponse(
        bool Success,
        string? RestoredBackup = null,
        string? RestoredVersion = null,
        bool CodeVersionRestored = false,
        string? Error = null,
        string? Message = null
    );

    /// <summary>
    /// Backup system status information
    /// </summary>
    public record BackupStatusResponse(
        string BackupDirectory,
        int TotalBackups,
        string TotalSize,
        string? OldestBackup,
        string? NewestBackup,
        int RetentionDays,
        int BackupsToCleanup
    );
}
