using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Implementation of health check service that verifies container health via Docker Compose
    /// </summary>
    public class HealthCheckService : IHealthCheckService
    {
        private readonly ISshService _sshService;
        private readonly ILogger<HealthCheckService> _logger;

        public HealthCheckService(ISshService sshService, ILogger<HealthCheckService> logger)
        {
            _sshService = sshService ?? throw new ArgumentNullException(nameof(sshService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<HealthCheckResult> CheckServicesHealthAsync(string[] composeFiles, string workingDirectory)
        {
            try
            {
                _logger.LogInformation("Checking health of services in {WorkingDirectory}", workingDirectory);

                // Get list of all services from compose files
                var servicesResult = await GetServicesFromComposeAsync(composeFiles, workingDirectory);
                if (!servicesResult.IsSuccess)
                {
                    _logger.LogError("Failed to get services list: {Error}", servicesResult.Error);
                    return HealthCheckResult.Failed($"Failed to get services list: {servicesResult.Error}");
                }

                var allServices = ParseServicesList(servicesResult.Output);
                if (!allServices.Any())
                {
                    _logger.LogWarning("No services found in compose files");
                    return HealthCheckResult.Healthy(new List<string>());
                }

                // Check health of each service
                var healthyServices = new List<string>();
                var unhealthyServices = new List<string>();

                foreach (var service in allServices)
                {
                    var isHealthy = await CheckSingleServiceHealthAsync(service, composeFiles, workingDirectory);
                    if (isHealthy)
                    {
                        healthyServices.Add(service);
                    }
                    else
                    {
                        unhealthyServices.Add(service);
                    }
                }

                _logger.LogInformation("Health check complete. Healthy: {HealthyCount}, Unhealthy: {UnhealthyCount}",
                    healthyServices.Count, unhealthyServices.Count);

                if (unhealthyServices.Any())
                {
                    // Determine if this is a critical failure based on service names
                    var criticalServices = new[] { "database", "api", "core", "main", "primary" };
                    var hasCriticalFailure = unhealthyServices.Any(s => 
                        criticalServices.Any(cs => s.ToLowerInvariant().Contains(cs)));

                    return HealthCheckResult.Unhealthy(healthyServices, unhealthyServices, hasCriticalFailure);
                }

                return HealthCheckResult.Healthy(healthyServices);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed with exception");
                return HealthCheckResult.Failed($"Health check failed: {ex.Message}");
            }
        }

        private async Task<SshCommandResult> GetServicesFromComposeAsync(string[] composeFiles, string workingDirectory)
        {
            var composeFilesArgs = string.Join(" ", composeFiles.Select(f => $"-f {f}"));
            var command = $"docker-compose {composeFilesArgs} config --services";
            
            return await _sshService.ExecuteCommandAsync(command, workingDirectory);
        }

        private List<string> ParseServicesList(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return new List<string>();

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim())
                         .Where(s => !string.IsNullOrEmpty(s))
                         .ToList();
        }

        private async Task<bool> CheckSingleServiceHealthAsync(string serviceName, string[] composeFiles, string workingDirectory)
        {
            try
            {
                _logger.LogDebug("Checking health of service: {ServiceName}", serviceName);

                var composeFilesArgs = string.Join(" ", composeFiles.Select(f => $"-f {f}"));
                var command = $"docker-compose {composeFilesArgs} ps --format json {serviceName}";
                
                var result = await _sshService.ExecuteCommandAsync(command, workingDirectory);
                if (!result.IsSuccess)
                {
                    _logger.LogWarning("Failed to get status for service {ServiceName}: {Error}", serviceName, result.Error);
                    return false;
                }

                // Parse JSON output to check service status
                var serviceStatus = ParseServiceStatus(result.Output);
                var isHealthy = serviceStatus?.State?.ToLowerInvariant() == "running";

                _logger.LogDebug("Service {ServiceName} status: {Status}, Healthy: {IsHealthy}", 
                    serviceName, serviceStatus?.State, isHealthy);

                return isHealthy;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception checking health of service {ServiceName}", serviceName);
                return false;
            }
        }

        private ServiceStatus? ParseServiceStatus(string jsonOutput)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonOutput))
                    return null;

                // Handle both single service and array responses
                if (jsonOutput.TrimStart().StartsWith("["))
                {
                    var services = JsonSerializer.Deserialize<ServiceStatus[]>(jsonOutput);
                    return services?.FirstOrDefault();
                }
                else
                {
                    return JsonSerializer.Deserialize<ServiceStatus>(jsonOutput);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse service status JSON: {Json}", jsonOutput);
                return null;
            }
        }

        private class ServiceStatus
        {
            public string? Name { get; set; }
            public string? State { get; set; }
            public string? Status { get; set; }
        }
    }
}