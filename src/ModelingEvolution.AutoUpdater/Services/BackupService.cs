using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Implementation of backup service that executes backup.sh and restore.sh scripts via SSH
    /// </summary>
    public class BackupService : IBackupService
    {
        private readonly ISshService _sshService;
        private readonly ILogger<BackupService> _logger;

        public BackupService(ISshService sshService, ILogger<BackupService> logger)
        {
            _sshService = sshService ?? throw new ArgumentNullException(nameof(sshService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> BackupScriptExistsAsync(string directory)
        {
            try
            {
                _logger.LogDebug("Checking if backup.sh exists in {Directory}", directory);
                
                var backupScriptPath = Path.Combine(directory, "backup.sh");
                var exists = await _sshService.FileExistsAsync(backupScriptPath);
                
                _logger.LogDebug("backup.sh exists: {Exists} in {Directory}", exists, directory);
                return exists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check if backup.sh exists in {Directory}", directory);
                return false;
            }
        }

        public async Task<BackupResult> CreateBackupAsync(string directory, string? version = null)
        {
            try
            {
                _logger.LogInformation("Creating backup in directory {Directory} with version {Version}",
                    directory, version ?? "unspecified");

                var versionArg = !string.IsNullOrEmpty(version) ? $" --version=\"{version}\"" : "";
                var command = $"sudo bash ./backup.sh{versionArg} --format=json";
                var result = await _sshService.ExecuteCommandAsync(command, TimeSpan.FromMinutes(5), directory);

                if (!result.IsSuccess)
                {
                    _logger.LogError("Backup script failed with exit code {ExitCode}: {Error}",
                        result.ExitCode, result.Error);

                    // Try to parse error response if possible
                    try
                    {
                        var errorResponse = JsonSerializer.Deserialize<BackupResponse>(result.Output);
                        if (errorResponse?.Error != null)
                        {
                            return BackupResult.CreateFailure(errorResponse.Error);
                        }
                    }
                    catch
                    {
                        // If JSON parsing fails, use stderr or generic message
                    }

                    return BackupResult.CreateFailure(
                        !string.IsNullOrEmpty(result.Error) ? result.Error : "Backup script execution failed");
                }

                // Parse successful backup response
                var backupResponse = JsonSerializer.Deserialize<BackupResponse>(result.Output);

                // Check if this is an error response (success = false)
                if (backupResponse?.Success == false)
                {
                    _logger.LogError("Backup script returned error: {Error}", backupResponse.Error);
                    return BackupResult.CreateFailure(backupResponse.Error ?? "Backup operation failed");
                }

                // Check if we have a valid file path
                if (string.IsNullOrEmpty(backupResponse?.File))
                {
                    _logger.LogError("Backup script returned invalid JSON response: {Output}", result.Output);
                    return BackupResult.CreateFailure("Backup script returned invalid response format");
                }

                _logger.LogInformation("Backup created successfully: {BackupFile}", backupResponse.File);
                return BackupResult.CreateSuccess(backupResponse.File);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create backup in {Directory}", directory);
                return BackupResult.CreateFailure($"Backup operation failed: {ex.Message}");
            }
        }

        public async Task<bool> RestoreScriptExistsAsync(string directory)
        {
            try
            {
                _logger.LogDebug("Checking if restore.sh exists in {Directory}", directory);
                
                var restoreScriptPath = Path.Combine(directory, "restore.sh");
                var exists = await _sshService.FileExistsAsync(restoreScriptPath);
                
                _logger.LogDebug("sudo bash restore.sh exists: {Exists} in {Directory}", exists, directory);
                return exists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check if restore.sh exists in {Directory}", directory);
                return false;
            }
        }

        public async Task<RestoreResult> RestoreBackupAsync(string directory, string backupFilePath)
        {
            try
            {
                _logger.LogInformation("Restoring backup from {BackupFile} in directory {Directory}", 
                    backupFilePath, directory);

                var command = $"sudo bash ./restore.sh --file=\"{backupFilePath}\" --format=json";
                var result = await _sshService.ExecuteCommandAsync(command, directory);

                if (!result.IsSuccess)
                {
                    _logger.LogError("Restore script failed with exit code {ExitCode}: {Error}", 
                        result.ExitCode, result.Error);
                    
                    // Try to parse error response if possible
                    try
                    {
                        var errorResponse = JsonSerializer.Deserialize<RestoreResponse>(result.Output);
                        if (errorResponse?.Error != null)
                        {
                            return RestoreResult.CreateFailure(errorResponse.Error);
                        }
                    }
                    catch
                    {
                        // If JSON parsing fails, use stderr or generic message
                    }
                    
                    return RestoreResult.CreateFailure(
                        !string.IsNullOrEmpty(result.Error) ? result.Error : "Restore script execution failed");
                }

                // Parse restore response
                var restoreResponse = JsonSerializer.Deserialize<RestoreResponse>(result.Output);
                if (restoreResponse == null)
                {
                    _logger.LogError("Restore script returned invalid JSON response: {Output}", result.Output);
                    return RestoreResult.CreateFailure("Restore script returned invalid response format");
                }

                if (!restoreResponse.Success)
                {
                    _logger.LogError("Restore script reported failure: {Error}", restoreResponse.Error);
                    return RestoreResult.CreateFailure(restoreResponse.Error ?? "Restore operation failed");
                }

                _logger.LogInformation("Backup restored successfully from {BackupFile}", backupFilePath);
                return RestoreResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore backup from {BackupFile} in {Directory}",
                    backupFilePath, directory);
                return RestoreResult.CreateFailure($"Restore operation failed: {ex.Message}");
            }
        }

        public async Task<BackupListResult> ListBackupsAsync(string directory)
        {
            try
            {
                _logger.LogInformation("Listing backups in directory {Directory}", directory);

                var command = "sudo bash ./backup.sh list --format=json";
                var result = await _sshService.ExecuteCommandAsync(command, directory);

                if (!result.IsSuccess)
                {
                    _logger.LogError("Backup list command failed: {Error}", result.Error);
                    return BackupListResult.CreateFailure(result.Error);
                }

                var response = JsonSerializer.Deserialize<BackupListResponse>(result.Output, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (response == null)
                {
                    return BackupListResult.CreateFailure("Failed to parse backup list response");
                }

                _logger.LogInformation("Found {Count} backups", response.TotalCount);
                return BackupListResult.CreateSuccess(response.Backups, response.TotalCount, response.TotalSizeBytes, response.TotalSize);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list backups in {Directory}", directory);
                return BackupListResult.CreateFailure($"List operation failed: {ex.Message}");
            }
        }
    }

    // JSON response model for backup.sh list command
    internal record BackupListResponse(
        List<BackupInfo> Backups,
        int TotalCount,
        long TotalSizeBytes,
        string TotalSize
    );
}