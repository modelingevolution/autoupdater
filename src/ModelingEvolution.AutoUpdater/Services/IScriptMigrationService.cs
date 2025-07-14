using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using ModelingEvolution.AutoUpdater.Models;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for discovering and executing migration scripts
    /// </summary>
    public interface IScriptMigrationService
    {
        /// <summary>
        /// Discovers all migration scripts in the specified directory
        /// </summary>
        /// <param name="directory">The directory to search for migration scripts</param>
        /// <returns>Collection of discovered migration scripts</returns>
        Task<IEnumerable<MigrationScript>> DiscoverScriptsAsync(string directory);

        /// <summary>
        /// Filters scripts that should be executed for a version migration
        /// </summary>
        /// <param name="scripts">All available migration scripts</param>
        /// <param name="fromVersion">The previous version (exclusive)</param>
        /// <param name="toVersion">The target version (inclusive)</param>
        /// <param name="excludeVersions">Set of versions to exclude (already executed scripts)</param>
        /// <returns>Scripts that should be executed, ordered by version</returns>
        Task<IEnumerable<MigrationScript>> FilterScriptsForMigrationAsync(
            IEnumerable<MigrationScript> scripts, 
            string? fromVersion, 
            string toVersion,
            ImmutableSortedSet<Version>? excludeVersions = null);

        /// <summary>
        /// Executes a collection of migration scripts in order
        /// </summary>
        /// <param name="scripts">The scripts to execute, should be pre-ordered</param>
        /// <param name="workingDirectory">The working directory for script execution</param>
        /// <returns>Collection of successfully executed script versions</returns>
        Task<IEnumerable<Version>> ExecuteScriptsAsync(IEnumerable<MigrationScript> scripts, string workingDirectory);

        /// <summary>
        /// Validates that a script file can be executed
        /// </summary>
        /// <param name="scriptPath">The path to the script file</param>
        /// <returns>True if the script is valid and can be executed</returns>
        Task<bool> ValidateScriptAsync(string scriptPath);
    }
}