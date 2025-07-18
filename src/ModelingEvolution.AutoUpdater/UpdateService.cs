using Microsoft.Extensions.Logging;
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
        private readonly ILogger<UpdateService> _logger;

        public UpdateService(
            DockerComposeConfigurationModel repository,
            UpdateHost updateHost,
            IGitService gitService,
            ILogger<UpdateService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _updateHost = updateHost ?? throw new ArgumentNullException(nameof(updateHost));
            _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
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
                var latestVersion = availableVersions.OrderByDescending(v => v.Version).FirstOrDefault();
                
                var upgradeAvailable = await _updateHost.CheckIsUpdateAvailable(config);

                packages.Add(new PackageInfo
                {
                    Name = packageName,
                    RepositoryUrl = config.RepositoryUrl,
                    CurrentVersion = currentVersion ?? "unknown",
                    LatestVersion = latestVersion?.FriendlyName ?? "unknown",
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
            var latestVersion = availableVersions.OrderByDescending(v => v.Version).FirstOrDefault();
            
            var upgradeAvailable = latestVersion != null &&
                                 currentVersion != null &&
                                 latestVersion.CompareTo(GitTagVersion.TryParse(currentVersion, out var parsed) ? parsed : null) > 0;

            return new PackageInfo
            {
                Name = config.FriendlyName,
                RepositoryUrl = config.RepositoryUrl,
                CurrentVersion = currentVersion ?? "unknown",
                LatestVersion = latestVersion?.FriendlyName ?? "unknown",
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
                var stateFile = Path.Combine(config.HostComposeFolderPath, "deployment.state.json");
                if (!File.Exists(stateFile))
                    return null;

                var stateContent = await File.ReadAllTextAsync(stateFile);
                var state = JsonSerializer.Deserialize<DeploymentState>(stateContent);
                return state?.Version;
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
        public PackageName Name { get; init; } = string.Empty;
        public string RepositoryUrl { get; init; } = string.Empty;
        public string CurrentVersion { get; init; } = string.Empty;
        public string LatestVersion { get; init; } = string.Empty;
        public bool UpgradeAvailable { get; init; }
        public DateTime LastChecked { get; init; }
    }

    public class PackageNotFoundException : Exception
    {
        public PackageNotFoundException(string message) : base(message) { }
        public PackageNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
}