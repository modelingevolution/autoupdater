using System.Threading.Tasks;
using ModelingEvolution.AutoUpdater;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for managing Docker authentication settings
    /// </summary>
    public interface IDockerAuthService
    {
        /// <summary>
        /// Updates the Docker authentication for a specific package
        /// </summary>
        /// <param name="packageName">The package name</param>
        /// <param name="dockerAuth">The new Docker authentication string</param>
        /// <returns>Task representing the async operation</returns>
        Task UpdateDockerAuthAsync(string packageName, string? dockerAuth);
        
        /// <summary>
        /// Gets the Docker authentication for a specific package
        /// </summary>
        /// <param name="packageName">The package name</param>
        /// <returns>The Docker authentication string, or null if not set</returns>
        Task<string?> GetDockerAuthAsync(string packageName);
    }
}