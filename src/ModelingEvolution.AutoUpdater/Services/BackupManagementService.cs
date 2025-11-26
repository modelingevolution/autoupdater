using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Implementation of backup management service
    /// </summary>
    public class BackupManagementService : IBackupManagementService
    {
        private readonly DockerComposeConfigurationModel _configModel;
        private readonly ISshService _sshService;
        private readonly IDeploymentStateProvider _deploymentStateProvider;
        private readonly ILogger<BackupManagementService> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public BackupManagementService(
            DockerComposeConfigurationModel configModel,
            ISshService sshService,
            IDeploymentStateProvider deploymentStateProvider,
            ILogger<BackupManagementService> logger)
        {
            _configModel = configModel ?? throw new ArgumentNullException(nameof(configModel));
            _sshService = sshService ?? throw new ArgumentNullException(nameof(sshService));
            _deploymentStateProvider = deploymentStateProvider ?? throw new ArgumentNullException(nameof(deploymentStateProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<BackupListResponse> ListBackupsAsync(PackageName packageName)
        {
            try
            {
                var config = _configModel.GetPackage(packageName);
                if (config == null)
                {
                    _logger.LogWarning("Package {PackageName} not found", packageName);
                    throw new PackageNotFoundException($"Package {packageName} not found");
                }

                if (!config.BackupEnabled)
                {
                    _logger.LogWarning("Backup not enabled for package {PackageName}", packageName);
                    return new BackupListResponse([], 0, 0, "0", "Backup not enabled for this package");
                }

                var scriptPath = Path.Combine(config.HostComposeFolderPath, config.BackupScriptPath ?? "es-backup-manage.sh");
                var command = $"sudo bash \"{scriptPath}\" list --format=json";

                _logger.LogDebug("Executing backup list command: {Command}", command);

                var result = await _sshService.ExecuteCommandAsync(command);

                if (!result.IsSuccess)
                {
                    _logger.LogError("Failed to list backups: {Error}", result.Error);
                    return new BackupListResponse([], 0, 0, "0", result.Error);
                }

                var response = JsonSerializer.Deserialize<BackupListResponse>(result.Output, JsonOptions);
                _logger.LogInformation("Listed {Count} backups for {PackageName}", response?.TotalCount ?? 0, packageName);

                return response ?? new BackupListResponse([], 0, 0, "0", "Failed to parse response");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing backups for {PackageName}", packageName);
                throw;
            }
        }

        public async Task<BackupCreateResponse> CreateBackupAsync(PackageName packageName, string? version = null)
        {
            try
            {
                var config = _configModel.GetPackage(packageName);
                if (config == null)
                {
                    _logger.LogWarning("Package {PackageName} not found", packageName);
                    throw new PackageNotFoundException($"Package {packageName} not found");
                }

                if (!config.BackupEnabled)
                {
                    _logger.LogWarning("Backup not enabled for package {PackageName}", packageName);
                    return new BackupCreateResponse(false, null, "Backup not enabled for this package", null);
                }

                // Get current version from deployment state if not provided
                if (string.IsNullOrEmpty(version))
                {
                    version = await _deploymentStateProvider.GetCurrentVersionAsync(config.HostComposeFolderPath);
                    _logger.LogInformation("Auto-detected version for backup: {Version}", version);
                }

                if (string.IsNullOrEmpty(version))
                {
                    version = "unknown";
                    _logger.LogWarning("Could not determine package version for backup");
                }

                var scriptPath = Path.Combine(config.HostComposeFolderPath, config.BackupScriptPath ?? "es-backup-manage.sh");
                var command = $"sudo bash \"{scriptPath}\" backup --version={version}";

                _logger.LogInformation("Creating backup for {PackageName} version {Version}", packageName, version);

                var result = await _sshService.ExecuteCommandAsync(command, TimeSpan.FromMinutes(5));

                if (!result.IsSuccess)
                {
                    _logger.LogError("Backup creation failed: {Error}", result.Error);
                    return new BackupCreateResponse(false, null, "Backup creation failed", result.Error);
                }

                _logger.LogInformation("Backup created successfully for {PackageName} version {Version}", packageName, version);
                return new BackupCreateResponse(true, null, null, "Backup created successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating backup for {PackageName}", packageName);
                throw;
            }
        }

        public async Task<BackupRestoreResponse> RestoreBackupAsync(PackageName packageName, string filename)
        {
            try
            {
                var config = _configModel.GetPackage(packageName);
                if (config == null)
                {
                    _logger.LogWarning("Package {PackageName} not found", packageName);
                    throw new PackageNotFoundException($"Package {packageName} not found");
                }

                if (!config.BackupEnabled)
                {
                    _logger.LogWarning("Backup not enabled for package {PackageName}", packageName);
                    return new BackupRestoreResponse(false, null, null, false, "Backup not enabled for this package", null);
                }

                // Validate filename to prevent path traversal
                if (filename.Contains("..") || filename.Contains("/") || filename.Contains("\\"))
                {
                    _logger.LogWarning("Invalid backup filename: {Filename}", filename);
                    return new BackupRestoreResponse(false, null, null, false, "Invalid backup filename", null);
                }

                var scriptPath = Path.Combine(config.HostComposeFolderPath, config.BackupScriptPath ?? "es-backup-manage.sh");
                var command = $"sudo bash \"{scriptPath}\" restore \"{filename}\" yes";

                _logger.LogInformation("Restoring {PackageName} from backup: {Filename}", packageName, filename);

                var result = await _sshService.ExecuteCommandAsync(command, TimeSpan.FromMinutes(10));

                if (!result.IsSuccess)
                {
                    _logger.LogError("Restore failed: {Error}", result.Error);
                    return new BackupRestoreResponse(false, null, null, false, "Restore failed", result.Error);
                }

                // Check output for version restoration info
                var codeRestored = result.Output.Contains("Successfully checked out to");
                var restoredVersion = "unknown"; // Could parse from output

                _logger.LogInformation("Restore completed for {PackageName}, code version restored: {CodeRestored}",
                    packageName, codeRestored);

                return new BackupRestoreResponse(true, filename, restoredVersion, codeRestored, null,
                    "Restore completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring backup for {PackageName}", packageName);
                throw;
            }
        }

        public async Task<BackupStatusResponse> GetBackupStatusAsync(PackageName packageName)
        {
            try
            {
                var config = _configModel.GetPackage(packageName);
                if (config == null)
                {
                    _logger.LogWarning("Package {PackageName} not found", packageName);
                    throw new PackageNotFoundException($"Package {packageName} not found");
                }

                if (!config.BackupEnabled)
                {
                    _logger.LogWarning("Backup not enabled for package {PackageName}", packageName);
                    return new BackupStatusResponse("", 0, "0", null, null, 0, 0);
                }

                var scriptPath = Path.Combine(config.HostComposeFolderPath, config.BackupScriptPath ?? "es-backup-manage.sh");
                var command = $"sudo bash \"{scriptPath}\" status";

                var result = await _sshService.ExecuteCommandAsync(command);

                if (!result.IsSuccess)
                {
                    _logger.LogError("Failed to get backup status: {Error}", result.Error);
                    return new BackupStatusResponse("", 0, "0", null, null, 0, 0);
                }

                // Parse status output (simplified for now)
                return new BackupStatusResponse("/var/docker/data/backups", 0, "0", null, null, 7, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting backup status for {PackageName}", packageName);
                throw;
            }
        }
    }
}
