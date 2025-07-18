using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
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

        

        public static async Task<IReadOnlyList<PackageVersion>> AvailableVersionsAsync(this DockerComposeConfiguration config, IGitService gitService, ILogger logger)
        {
            return await gitService.GetAvailableVersionsAsync(config.RepositoryLocation);
        }

        public static async Task<PackageVersion[]> VersionsAsync(this DockerComposeConfiguration config, IGitService gitService, ILogger logger)
        {
            var versions = await gitService.GetAvailableVersionsAsync(config.RepositoryLocation);
            return versions.ToArray();
        }

        public static async Task<bool> PullAsync(this DockerComposeConfiguration config, IGitService gitService, ILogger logger)
        {
            return await gitService.PullLatestAsync(config.RepositoryLocation);
        }

        public static async Task CheckoutAsync(this DockerComposeConfiguration config, PackageVersion version, IGitService gitService, ILogger logger)
        {
            await gitService.CheckoutVersionAsync(config.RepositoryLocation, version.ToString());
        }
    }
}