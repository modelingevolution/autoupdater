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

        public async Task<BackupResult> CreateBackupAsync(string directory)
        {
            try
            {
                _logger.LogInformation("Creating backup in directory {Directory}", directory);

                var command = "sudo ./backup.sh --format=json";
                var result = await _sshService.ExecuteCommandAsync(command, directory);

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
                if (backupResponse?.File == null)
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
                
                _logger.LogDebug("restore.sh exists: {Exists} in {Directory}", exists, directory);
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

                var command = $"./restore.sh --file=\"{backupFilePath}\" --format=json";
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
    }
}