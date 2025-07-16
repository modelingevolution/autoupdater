using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Extensions
{
    /// <summary>
    /// Extension methods for DockerComposeConfiguration using double-dispatch pattern
    /// </summary>
    public static class DockerComposeConfigurationExtensions
    {
        public static async Task<bool> CloneRepositoryAsync(this DockerComposeConfiguration config, IGitService gitService, ILogger logger)
        {
            return await gitService.CloneRepositoryAsync(config.RepositoryUrl, config.RepositoryLocation);
        }

        public static async Task<bool> IsUpgradeAvailableAsync(this DockerComposeConfiguration config, IGitService gitService, ILogger logger)
        {
            var availableUpgrade = await config.AvailableUpgradeAsync(gitService, logger);
            return availableUpgrade != null;
        }

        public static async Task<GitTagVersion?> AvailableUpgradeAsync(this DockerComposeConfiguration config, IGitService gitService, ILogger logger)
        {
            var availableVersions = await config.AvailableVersionsAsync(gitService, logger);
            var currentVersion = config.CurrentVersion;

            if (currentVersion == null)
            {
                return availableVersions.OrderByDescending(v => v.Version).FirstOrDefault();
            }

            if(GitTagVersion.TryParse(currentVersion, out var gv)) 
            return availableVersions
                .Where(v => v.Version > gv.Version)
                .OrderByDescending(v => v.Version)
                .FirstOrDefault();
            throw new InvalidOperationException($"Current version '{currentVersion}' is not a valid GitTagVersion");
        }

        public static async Task<IReadOnlyList<GitTagVersion>> AvailableVersionsAsync(this DockerComposeConfiguration config, IGitService gitService, ILogger logger)
        {
            return await gitService.GetAvailableVersionsAsync(config.RepositoryLocation);
        }

        public static async Task<GitTagVersion[]> VersionsAsync(this DockerComposeConfiguration config, IGitService gitService, ILogger logger)
        {
            var versions = await gitService.GetAvailableVersionsAsync(config.RepositoryLocation);
            return versions.ToArray();
        }

        public static async Task<bool> PullAsync(this DockerComposeConfiguration config, IGitService gitService, ILogger logger)
        {
            return await gitService.PullLatestAsync(config.RepositoryLocation);
        }

        public static async Task CheckoutAsync(this DockerComposeConfiguration config, GitTagVersion version, IGitService gitService, ILogger logger)
        {
            await gitService.CheckoutVersionAsync(config.RepositoryLocation, version.FriendlyName);
        }
    }
}