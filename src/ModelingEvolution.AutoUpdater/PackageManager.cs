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
            try
            {
                _logger.LogInformation("Starting restore for package: {PackageName} from backup: {BackupFilename}",
                    config.FriendlyName, backupFilename);

                // Get backup metadata to find version and git tag information
                var backupListResult = await _backupService.ListBackupsAsync(config.HostComposeFolderPath);
                if (!backupListResult.Success)
                {
                    _logger.LogError("Failed to list backups: {Error}", backupListResult.Error);
                    return RestoreResult.CreateFailure($"Failed to get backup metadata: {backupListResult.Error}");
                }

                var backupInfo = backupListResult.Backups.FirstOrDefault(b => b.Filename == backupFilename);
                if (backupInfo == null)
                {
                    _logger.LogError("Backup file not found: {BackupFilename}", backupFilename);
                    return RestoreResult.CreateFailure($"Backup file not found: {backupFilename}");
                }

                _logger.LogInformation("Found backup metadata - Version: {Version}, GitTag: {GitTagExists}",
                    backupInfo.Version, backupInfo.GitTagExists);

                // Get architecture and compose files
                using var sshService = await _sshConnectionManager.CreateSshServiceAsync();
                var architecture = await sshService.GetArchitectureAsync();
                var composeFiles = await _dockerComposeService.GetComposeFiles(config.HostComposeFolderPath, architecture);

                // Step 1: Stop running services
                _logger.LogInformation("Stopping services for package: {PackageName}", config.FriendlyName);
                await _dockerComposeService.StopServicesAsync(composeFiles, config.HostComposeFolderPath);
                _logger.LogInformation("Services stopped successfully");

                // Step 2: Restore the backup
                _logger.LogInformation("Restoring backup: {BackupFilename}", backupFilename);
                var restoreResult = await _backupService.RestoreBackupAsync(config.HostComposeFolderPath, backupFilename);
                if (!restoreResult.Success)
                {
                    _logger.LogError("Backup restore failed: {Error}", restoreResult.Error);
                    // Try to start services again even if restore failed
                    await _dockerComposeService.StartServicesAsync(composeFiles, config.HostComposeFolderPath);
                    return restoreResult;
                }
                _logger.LogInformation("Backup restored successfully");

                // Step 3: Checkout git tag if available
                if (backupInfo.GitTagExists && !string.IsNullOrEmpty(backupInfo.Version))
                {
                    try
                    {
                        _logger.LogInformation("Checking out git tag: {Version}", backupInfo.Version);
                        await _gitService.CheckoutVersionAsync(config.HostComposeFolderPath, backupInfo.Version);
                        _logger.LogInformation("Git tag checked out successfully: {Version}", backupInfo.Version);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to checkout git tag: {Version}. Continuing with current code.", backupInfo.Version);
                        // Don't fail the restore if git checkout fails - continue with current code
                    }
                }
                else
                {
                    _logger.LogInformation("No git tag available for this backup, continuing with current code");
                }

                // Step 4: Start services
                _logger.LogInformation("Starting services for package: {PackageName}", config.FriendlyName);
                await _dockerComposeService.StartServicesAsync(composeFiles, config.HostComposeFolderPath);
                _logger.LogInformation("Services started successfully");

                _logger.LogInformation("Package restore completed successfully: {PackageName}", config.FriendlyName);
                return RestoreResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring package: {PackageName} from backup: {BackupFilename}",
                    config.FriendlyName, backupFilename);
                return RestoreResult.CreateFailure($"Restore operation failed: {ex.Message}");
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