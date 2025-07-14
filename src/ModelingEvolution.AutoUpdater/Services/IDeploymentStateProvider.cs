using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Provides deployment state persistence and retrieval
    /// </summary>
    public interface IDeploymentStateProvider
    {
        /// <summary>
        /// Gets the current deployment state from storage
        /// </summary>
        /// <param name="deploymentPath">Path to the deployment directory</param>
        /// <returns>The deployment state if exists, null otherwise</returns>
        Task<DeploymentState?> GetDeploymentStateAsync(string deploymentPath);

        /// <summary>
        /// Saves the deployment state to storage
        /// </summary>
        /// <param name="deploymentPath">Path to the deployment directory</param>
        /// <param name="state">The deployment state to save</param>
        Task SaveDeploymentStateAsync(string deploymentPath, DeploymentState state);

        /// <summary>
        /// Gets the current version from deployment state
        /// </summary>
        /// <param name="deploymentPath">Path to the deployment directory</param>
        /// <returns>The current version if exists, null otherwise</returns>
        Task<string?> GetCurrentVersionAsync(string deploymentPath);

        /// <summary>
        /// Checks if deployment state exists
        /// </summary>
        /// <param name="deploymentPath">Path to the deployment directory</param>
        /// <returns>True if deployment state exists, false otherwise</returns>
        Task<bool> DeploymentStateExistsAsync(string deploymentPath);
    }
}