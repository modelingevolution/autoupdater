using System.Collections.Generic;
using System.Threading.Tasks;
using ModelingEvolution.AutoUpdater.Models;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for Docker Compose operations
    /// </summary>
    public interface IDockerComposeService
    {
        /// <summary>
        /// Gets the appropriate Docker Compose files for the target architecture
        /// </summary>
        /// <param name="composeDirectory">The directory containing compose files</param>
        /// <param name="architecture">The target architecture (e.g., "x64", "arm64")</param>
        /// <returns>Array of compose file paths to use</returns>
        Task<string[]> GetComposeFilesForArchitectureAsync(string composeDirectory, string architecture);

        /// <summary>
        /// Gets the current status of a Docker Compose project
        /// </summary>
        /// <param name="projectName">The name of the compose project</param>
        /// <returns>The status of the compose project</returns>
        Task<ComposeProjectStatus> GetProjectStatusAsync(string projectName);

        /// <summary>
        /// Starts Docker Compose services using the specified compose files
        /// </summary>
        /// <param name="composeFiles">The compose files to use</param>
        /// <param name="workingDirectory">The working directory for the compose command</param>
        Task StartServicesAsync(string[] composeFiles, string workingDirectory);

        /// <summary>
        /// Stops Docker Compose services for the specified project
        /// </summary>
        /// <param name="projectName">The name of the compose project to stop</param>
        Task StopServicesAsync(string projectName);

        /// <summary>
        /// Pulls the latest images for all services in the compose files
        /// </summary>
        /// <param name="composeFiles">The compose files to pull images for</param>
        /// <param name="workingDirectory">The working directory for the compose command</param>
        Task PullImagesAsync(string[] composeFiles, string workingDirectory);

        /// <summary>
        /// Gets volume mappings for the specified container
        /// </summary>
        /// <param name="containerId">The container ID to get volume mappings for</param>
        /// <returns>Dictionary of host path to container path mappings</returns>
        Task<IDictionary<string, string>> GetVolumeMappingsAsync(string containerId);
    }
}