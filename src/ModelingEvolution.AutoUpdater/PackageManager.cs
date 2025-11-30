using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Extensions;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater
{
    public class PackageManager
    {
        private readonly DockerComposeConfigurationModel _repository;
        private readonly UpdateHost _updateHost;
        private readonly IGitService _gitService;
        private readonly IDeploymentStateProvider _deploymentStateProvider;
        private readonly IBackupService _backupService;
        private readonly IDockerComposeService _dockerComposeService;
        private readonly ISshConnectionManager _sshConnectionManager;
        private readonly ILogger<PackageManager> _logger;

        public PackageManager(
            DockerComposeConfigurationModel repository,
            UpdateHost updateHost,
            IGitService gitService,
            IDeploymentStateProvider deploymentStateProvider,
            IBackupService backupService,
            IDockerComposeService dockerComposeService,
            ISshConnectionManager sshConnectionManager,
            ILogger<PackageManager> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _updateHost = updateHost ?? throw new ArgumentNullException(nameof(updateHost));
            _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
            _deploymentStateProvider = deploymentStateProvider ?? throw new ArgumentNullException(nameof(deploymentStateProvider));
            _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
            _dockerComposeService = dockerComposeService ?? throw new ArgumentNullException(nameof(dockerComposeService));
            _sshConnectionManager = sshConnectionManager ?? throw new ArgumentNullException(nameof(sshConnectionManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task UpdateAllAsync()
        {
            _logger.LogInformation("Starting update process for all packages");

            var packages = _repository.GetPackages().ToArray();
            foreach (var package in packages)
            {
                try
                {
                    _logger.LogInformation("Updating package: {PackageName}", package.FriendlyName);
                    await UpdatePackageAsync(package);
                    _logger.LogInformation("Successfully updated package: {PackageName}", package.FriendlyName);
                }
                catch (RestartPendingException ex)
                {
                    _logger.LogInformation("Package: {PackageName} triggers restart.", package.FriendlyName);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update package: {PackageName}", package.FriendlyName);
                    throw;
                }
            }

            _logger.LogInformation("Update process completed for all packages");
        }

        public async Task<UpdateResult> UpdatePackageAsync(DockerComposeConfiguration configuration)
        {
            return await _updateHost.UpdateAsync(configuration);
        }

        public async Task<IEnumerable<PackageInfo>> GetPackagesAsync()
        {
            var packages = new List<PackageInfo>();

            foreach (var config in _repository.GetPackages())
            {
                var packageName = config.FriendlyName;
                var currentVersion = await GetCurrentVersionAsync(config);
                var availableVersions = await config.AvailableVersionsAsync(_gitService, _logger);
                var latestVersion = availableVersions.OrderByDescending(v => v).FirstOrDefault();

                var upgradeAvailable = await _updateHost.CheckIsUpdateAvailable(config);

                packages.Add(new PackageInfo
                {
                    Name = packageName,
                    RepositoryUrl = config.RepositoryUrl,
                    CurrentVersion = currentVersion != null ? (PackageVersion?)PackageVersion.Parse(currentVersion) : null,
                    LatestVersion = latestVersion,
                    UpgradeAvailable = upgradeAvailable,
                    LastChecked = DateTime.UtcNow
                });
            }

            return packages;
        }

        public async Task<PackageInfo?> GetPackageAsync(PackageName packageName)
        {
            var config = _repository.GetPackage(packageName);

            if (config == null)
                return null;

            var currentVersion = await GetCurrentVersionAsync(config);
            var availableVersions = await config.AvailableVersionsAsync(_gitService, _logger);
            var latestVersion = availableVersions.OrderByDescending(v => v).FirstOrDefault();
            
            var upgradeAvailable = !latestVersion.IsEmpty &&
                                 currentVersion != null &&
                                 PackageVersion.TryParse(currentVersion, out var parsed) && latestVersion.CompareTo(parsed) > 0;

            return new PackageInfo
            {
                Name = config.FriendlyName,
                RepositoryUrl = config.RepositoryUrl,
                CurrentVersion = currentVersion != null ? (PackageVersion?)PackageVersion.Parse(currentVersion) : null,
                LatestVersion = latestVersion,
                UpgradeAvailable = upgradeAvailable,
                LastChecked = DateTime.UtcNow
            };
        }

        public async Task<UpdateResult> TriggerUpdateAsync(PackageName packageName)
        {
            var config = _repository.GetPackage(packageName);

            if (config == null)
            {
                throw new PackageNotFoundException($"Package '{packageName}' not found");
            }

            _logger.LogInformation("Starting update for package {PackageName}", packageName);
            var result = await UpdatePackageAsync(config);

            if (result.Success)
            {
                _logger.LogInformation("Update completed for package {PackageName}", packageName);
            }
            else
            {
                _logger.LogError("Update failed for package {PackageName}: {Error}", packageName, result.ErrorMessage);
            }

            return result;
        }

        public async Task<BackupResult> BackupPackageAsync(DockerComposeConfiguration config, string? version = null)
        {
            try
            {
                _logger.LogInformation("Starting backup for package: {PackageName}", config.FriendlyName);

                // Auto-detect version if not provided
                if (string.IsNullOrEmpty(version))
                {
                    version = await GetCurrentVersionAsync(config);
                    _logger.LogInformation("Auto-detected version: {Version}", version ?? "unknown");
                }

                if (string.IsNullOrEmpty(version))
                {
                    version = "unknown";
                }

                // Create backup with version metadata
                var result = await _backupService.CreateBackupAsync(config.HostComposeFolderPath, version);

                if (result.Success)
                {
                    _logger.LogInformation("Backup created successfully for package: {PackageName}, version: {Version}, file: {BackupFile}",
                        config.FriendlyName, version, result.BackupFilePath);
                }
                else
                {
                    _logger.LogError("Backup failed for package: {PackageName}, error: {Error}",
                        config.FriendlyName, result.Error);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating backup for package: {PackageName}", config.FriendlyName);
                return BackupResult.CreateFailure($"Backup operation failed: {ex.Message}");
            }
        }

        public async Task<RestoreResult> RestorePackageAsync(DockerComposeConfiguration config, string backupFilename)
        {
            string? originalVersion = null;
            bool dataRestored = false;

            try
            {
                _logger.LogInformation("Starting restore for package: {PackageName} from backup: {BackupFilename}",
                    config.FriendlyName, backupFilename);

                // Step 0: Get current version for potential rollback
                originalVersion = await GetCurrentVersionAsync(config);
                _logger.LogInformation("Current version before restore: {Version}", originalVersion ?? "unknown");

                // Get backup metadata to find version information
                var backupListResult = await _backupService.ListBackupsAsync(config.HostComposeFolderPath);
                if (!backupListResult.Success)
                {
                    _logger.LogError("Failed to list backups: {Error}", backupListResult.Error);
                    return RestoreResult.CreateFailure($"Failed to get backup metadata: {backupListResult.Error}");
                }

                var backupInfo = backupListResult.Backups.FirstOrDefault(b => b.Filename == backupFilename);
                if (backupInfo == null)
                {
                    _logger.LogError("Backup file not found in list: {BackupFilename}", backupFilename);
                    return RestoreResult.CreateFailure($"Backup file not found: {backupFilename}");
                }

                _logger.LogInformation("Found backup metadata - Version: {Version}", backupInfo.Version);

                // Step 1: Git checkout tag (backup version)
                if (!string.IsNullOrEmpty(backupInfo.Version))
                {
                    try
                    {
                        _logger.LogInformation("Step 1: Checking out git tag: {Version}", backupInfo.Version);
                        await _gitService.CheckoutVersionAsync(config.HostComposeFolderPath, backupInfo.Version);
                        _logger.LogInformation("Git tag checked out successfully: {Version}", backupInfo.Version);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Step 1 failed: Cannot checkout git tag: {Version}", backupInfo.Version);
                        return RestoreResult.CreateFailure($"Failed to checkout version {backupInfo.Version}: {ex.Message}");
                    }
                }
                else
                {
                    _logger.LogWarning("No version information in backup metadata, skipping git checkout");
                }

                // Step 2: Check if backup file exists
                using var sshService = await _sshConnectionManager.CreateSshServiceAsync();
                if (!string.IsNullOrEmpty(backupInfo.FullPath))
                {
                    _logger.LogInformation("Step 2: Checking if backup file exists: {FullPath}", backupInfo.FullPath);
                    if (!await sshService.FileExistsAsync(backupInfo.FullPath))
                    {
                        _logger.LogError("Step 2 failed: Backup file does not exist: {FullPath}", backupInfo.FullPath);
                        await RollbackGitVersionAsync(config, originalVersion, "Backup file not found");
                        return RestoreResult.CreateFailure($"Backup file does not exist: {backupInfo.FullPath}");
                    }
                }

                // Get architecture and compose files
                var architecture = await sshService.GetArchitectureAsync();
                var composeFiles = await _dockerComposeService.GetComposeFiles(config.HostComposeFolderPath, architecture);

                // Step 3: Pull docker images (30 min timeout)
                try
                {
                    _logger.LogInformation("Step 3: Pulling Docker images (30 min timeout)");
                    await _dockerComposeService.PullAsync(composeFiles, config.HostComposeFolderPath, TimeSpan.FromMinutes(30));
                    _logger.LogInformation("Docker images pulled successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Step 3 failed: Cannot pull Docker images - Docker daemon may be broken");
                    await RollbackGitVersionAsync(config, originalVersion, "Docker pull failed - daemon may be broken");
                    return RestoreResult.CreateFailure($"Failed to pull Docker images: {ex.Message}. Docker daemon may be broken.");
                }

                // Step 4: Stop services (docker compose down with built-in force fallback)
                try
                {
                    _logger.LogInformation("Step 4: Stopping services");
                    await _dockerComposeService.StopServicesAsync(composeFiles, config.HostComposeFolderPath);
                    _logger.LogInformation("Services stopped successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Step 4 failed: Cannot stop services - Docker system may be broken");
                    await RollbackGitVersionAsync(config, originalVersion, "Failed to stop services - Docker system broken");
                    return RestoreResult.CreateFailure($"Failed to stop services: {ex.Message}. Docker system may be broken, services in unknown state.");
                }

                // Step 5: Restore data (POINT OF NO RETURN)
                _logger.LogInformation("Step 5: Restoring backup data (POINT OF NO RETURN)");
                var restoreResult = await _backupService.RestoreBackupAsync(config.HostComposeFolderPath, backupFilename);
                if (!restoreResult.Success)
                {
                    _logger.LogCritical("Step 5 failed: Data restore failed after stopping services - CRITICAL STATE");
                    dataRestored = false;
                    return RestoreResult.CreateFailure($"CRITICAL: Data restore failed: {restoreResult.Error}. Services stopped, old data lost. Manual intervention required.");
                }
                dataRestored = true;
                _logger.LogInformation("Backup data restored successfully - committed to backup version");

                // Step 6: Start services
                try
                {
                    _logger.LogInformation("Step 6: Starting services");
                    await _dockerComposeService.StartServicesAsync(composeFiles, config.HostComposeFolderPath);
                    _logger.LogInformation("Services started successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Step 6 failed: Services won't start (partial success - data already restored)");
                    // Update version anyway since data is restored
                    await UpdateVersionAfterRestoreAsync(config, backupInfo.Version);
                    return RestoreResult.CreateFailure($"PARTIAL SUCCESS: Data restored to version {backupInfo.Version}, but services failed to start: {ex.Message}");
                }

                // Step 7: Update current version file
                await UpdateVersionAfterRestoreAsync(config, backupInfo.Version);

                _logger.LogInformation("Restore completed successfully: {PackageName} restored to version {Version}",
                    config.FriendlyName, backupInfo.Version);
                return RestoreResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during restore: {PackageName} from backup: {BackupFilename}",
                    config.FriendlyName, backupFilename);

                if (dataRestored)
                {
                    return RestoreResult.CreateFailure($"PARTIAL SUCCESS: Data restored but error occurred: {ex.Message}");
                }

                return RestoreResult.CreateFailure($"Restore operation failed: {ex.Message}");
            }
        }

        private async Task RollbackGitVersionAsync(DockerComposeConfiguration config, string? originalVersion, string reason)
        {
            if (string.IsNullOrEmpty(originalVersion))
            {
                _logger.LogWarning("Cannot rollback git version: original version unknown. Reason: {Reason}", reason);
                return;
            }

            try
            {
                _logger.LogInformation("Rolling back git to previous version: {Version}. Reason: {Reason}",
                    originalVersion, reason);
                await _gitService.CheckoutVersionAsync(config.HostComposeFolderPath, originalVersion);
                _logger.LogInformation("Git rolled back successfully to {Version}", originalVersion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rollback git to version {Version}", originalVersion);
            }
        }

        private async Task UpdateVersionAfterRestoreAsync(DockerComposeConfiguration config, string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                _logger.LogWarning("Cannot update version file: version is empty");
                return;
            }

            try
            {
                _logger.LogInformation("Step 7: Updating version file to {Version}", version);
                var packageVersion = PackageVersion.Parse(version);
                var deploymentState = new DeploymentState(packageVersion, DateTime.UtcNow);
                await _deploymentStateProvider.SaveDeploymentStateAsync(config.HostComposeFolderPath, deploymentState);
                _logger.LogInformation("Version file updated successfully to {Version}", version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update version file to {Version}", version);
            }
        }

        private async Task<string?> GetCurrentVersionAsync(DockerComposeConfiguration config)
        {
            try
            {
                return await _deploymentStateProvider.GetCurrentVersionAsync(config.HostComposeFolderPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get current version for package {PackageName}", config.FriendlyName);
                return null;
            }
        }
    }

    public record PackageInfo
    {
        public PackageName Name { get; init; } = PackageName.Empty;
        public string RepositoryUrl { get; init; } = string.Empty;
        public PackageVersion? CurrentVersion { get; init; }
        public PackageVersion LatestVersion { get; init; } = PackageVersion.Empty;
        public bool UpgradeAvailable { get; init; }
        public DateTime LastChecked { get; init; }
    }

    public class PackageNotFoundException : Exception
    {
        public PackageNotFoundException(string message) : base(message) { }
        public PackageNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
}