using System.Collections.Generic;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for Git repository operations
    /// </summary>
    public interface IGitService
    {
        /// <summary>
        /// Clones a Git repository to the specified target path
        /// </summary>
        /// <param name="repositoryUrl">The URL of the repository to clone</param>
        /// <param name="targetPath">The local path where the repository should be cloned</param>
        /// <returns>True if the clone operation was successful, false otherwise</returns>
        Task<bool> CloneRepositoryAsync(string repositoryUrl, string targetPath);

        /// <summary>
        /// Pulls the latest changes from the remote repository
        /// </summary>
        /// <param name="repositoryPath">The local path of the Git repository</param>
        /// <returns>True if there were new changes pulled, false if already up to date</returns>
        Task<bool> PullLatestAsync(string repositoryPath);

        /// <summary>
        /// Checks out a specific version/tag in the repository
        /// </summary>
        /// <param name="repositoryPath">The local path of the Git repository</param>
        /// <param name="version">The version/tag to checkout</param>
        Task CheckoutVersionAsync(string repositoryPath, string version);

        /// <summary>
        /// Gets all available versions (tags) from the repository
        /// </summary>
        /// <param name="repositoryPath">The local path of the Git repository</param>
        /// <returns>Collection of available versions ordered by semantic version (descending)</returns>
        Task<IReadOnlyList<GitTagVersion>> GetAvailableVersionsAsync(string repositoryPath);


        /// <summary>
        /// Checks if the specified path is a Git repository
        /// </summary>
        /// <param name="path">The path to check</param>
        /// <returns>True if the path contains a Git repository</returns>
        bool IsGitRepository(string path);

        /// <summary>
        /// Fetches latest tags and references from the remote repository
        /// </summary>
        /// <param name="repositoryPath">The local path of the Git repository</param>
        Task FetchAsync(string repositoryPath);

        /// <summary>
        /// Initializes a Git repository in an existing directory and adds a remote origin
        /// </summary>
        /// <param name="repositoryPath">The local path to initialize as a Git repository</param>
        /// <param name="remoteUrl">The URL of the remote repository to add as origin</param>
        /// <returns>True if the initialization was successful, false otherwise</returns>
        Task<bool> InitializeRepositoryAsync(string repositoryPath, string remoteUrl);
    }
}