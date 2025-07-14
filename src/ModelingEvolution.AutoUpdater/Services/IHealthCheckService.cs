using System.Collections.Generic;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for checking container and service health after deployment
    /// </summary>
    public interface IHealthCheckService
    {
        /// <summary>
        /// Checks the health of all services in the deployment
        /// </summary>
        /// <param name="composeFiles">Docker compose files to check</param>
        /// <param name="workingDirectory">Working directory for docker commands</param>
        /// <returns>Health check result with service status</returns>
        Task<HealthCheckResult> CheckServicesHealthAsync(string[] composeFiles, string workingDirectory);
    }

    /// <summary>
    /// Result of health check operation
    /// </summary>
    public class HealthCheckResult
    {
        public bool AllHealthy { get; set; }
        public bool CriticalFailure { get; set; }
        public List<string> HealthyServices { get; set; } = new();
        public List<string> UnhealthyServices { get; set; } = new();
        public string? ErrorMessage { get; set; }

        public static HealthCheckResult Healthy(List<string> services)
        {
            return new HealthCheckResult
            {
                AllHealthy = true,
                CriticalFailure = false,
                HealthyServices = services
            };
        }

        public static HealthCheckResult Unhealthy(List<string> healthyServices, List<string> unhealthyServices, bool critical = false)
        {
            return new HealthCheckResult
            {
                AllHealthy = false,
                CriticalFailure = critical,
                HealthyServices = healthyServices,
                UnhealthyServices = unhealthyServices
            };
        }

        public static HealthCheckResult Failed(string error)
        {
            return new HealthCheckResult
            {
                AllHealthy = false,
                CriticalFailure = true,
                ErrorMessage = error
            };
        }
    }
}