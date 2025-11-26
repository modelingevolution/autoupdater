using System;
using System.Collections.Generic;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Information about a single backup
    /// </summary>
    public record BackupInfo(
        string Filename,
        string Version,
        bool GitTagExists,
        string Size,
        long SizeBytes,
        DateTime CreatedDate,
        string FullPath
    );

    /// <summary>
    /// Result of listing backups
    /// </summary>
    public record BackupListResult(
        bool Success,
        List<BackupInfo> Backups,
        int TotalCount,
        long TotalSizeBytes,
        string TotalSize,
        string? Error = null
    )
    {
        public static BackupListResult CreateSuccess(List<BackupInfo> backups, int totalCount, long totalSizeBytes, string totalSize) =>
            new(true, backups, totalCount, totalSizeBytes, totalSize, null);

        public static BackupListResult CreateFailure(string error) =>
            new(false, new List<BackupInfo>(), 0, 0, "0", error);
    }
}
