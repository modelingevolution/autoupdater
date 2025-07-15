using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelingEvolution.RuntimeConfiguration;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Implementation of Docker authentication service using runtime configuration
    /// </summary>
    public class DockerAuthService : IDockerAuthService
    {
        private readonly IRuntimeConfiguration _runtimeConfig;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DockerAuthService> _logger;
        private const string DockerAuthSection = "DockerAuth";

        public DockerAuthService(
            IRuntimeConfiguration runtimeConfig, 
            IConfiguration configuration,
            ILogger<DockerAuthService> logger)
        {
            _runtimeConfig = runtimeConfig ?? throw new ArgumentNullException(nameof(runtimeConfig));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task UpdateDockerAuthAsync(string packageName, string? dockerAuth)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                throw new ArgumentException("Package name cannot be null or empty", nameof(packageName));
            }

            _logger.LogInformation("Updating Docker authentication for package {PackageName}", packageName);

            if (string.IsNullOrWhiteSpace(dockerAuth))
            {
                // If dockerAuth is null or empty, reset to default
                await _runtimeConfig.ResetToDefaultAsync(DockerAuthSection, packageName);
                _logger.LogInformation("Docker authentication for package {PackageName} reset to default", packageName);
            }
            else
            {
                // Save the new authentication
                await _runtimeConfig.Save(DockerAuthSection, packageName, dockerAuth);
                _logger.LogInformation("Docker authentication for package {PackageName} updated", packageName);
            }
        }

        public Task<string?> GetDockerAuthAsync(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                throw new ArgumentException("Package name cannot be null or empty", nameof(packageName));
            }

            // Get from configuration (which includes runtime configuration)
            var dockerAuth = _configuration.GetValue<string>($"{DockerAuthSection}:{packageName}");
            return Task.FromResult(dockerAuth);
        }
    }
}