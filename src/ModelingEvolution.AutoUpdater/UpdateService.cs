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
    public class UpdateService
    {
        private readonly DockerComposeConfigurationModel _repository;
        private readonly UpdateHost _updateHost;
        private readonly IGitService _gitService;
        private readonly IDeploymentStateProvider _deploymentStateProvider;
        private readonly ILogger<UpdateService> _logger;

        public UpdateService(
            DockerComposeConfigurationModel repository,
            UpdateHost updateHost,
            IGitService gitService,
            IDeploymentStateProvider deploymentStateProvider,
            ILogger<UpdateService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _updateHost = updateHost ?? throw new ArgumentNullException(nameof(updateHost));
            _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
            _deploymentStateProvider = deploymentStateProvider ?? throw new ArgumentNullException(nameof(deploymentStateProvider));
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