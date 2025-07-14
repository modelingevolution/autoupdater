using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Version = LibGit2Sharp.Version;

namespace ModelingEvolution.AutoUpdater.Extensions
{
    /// <summary>
    /// Extension methods for DockerComposeConfiguration using double-dispatch pattern
    /// </summary>
    public static class DockerComposeConfigurationExtensions
    {
        public static bool CloneRepository(this DockerComposeConfiguration config, ILogger logger)
        {
            try
            {
                logger.LogInformation("Cloning repository {RepositoryUrl} to {RepositoryLocation}", 
                    config.RepositoryUrl, config.RepositoryLocation);

                if (Directory.Exists(config.RepositoryLocation))
                {
                    logger.LogInformation("Repository already exists at {RepositoryLocation}", config.RepositoryLocation);
                    return true;
                }

                Directory.CreateDirectory(config.RepositoryLocation);
                Repository.Clone(config.RepositoryUrl, config.RepositoryLocation);

                logger.LogInformation("Repository cloned successfully to {RepositoryLocation}", config.RepositoryLocation);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clone repository {RepositoryUrl} to {RepositoryLocation}", 
                    config.RepositoryUrl, config.RepositoryLocation);
                return false;
            }
        }

        public static bool IsUpgradeAvailable(this DockerComposeConfiguration config, ILogger logger)
        {
            var availableUpgrade = config.AvailableUpgrade(logger);
            return availableUpgrade != null;
        }

        public static GitTagVersion? AvailableUpgrade(this DockerComposeConfiguration config, ILogger logger)
        {
            var availableVersions = config.AvailableVersions(logger);
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

        public static IEnumerable<GitTagVersion> AvailableVersions(this DockerComposeConfiguration config, ILogger logger)
        {
            return config.Versions(logger).Where(v => v != null);
        }

        public static IEnumerable<GitTagVersion> Versions(this DockerComposeConfiguration config, ILogger logger)
        {
            try
            {
                if (!config.IsGitVersioned)
                {
                    logger.LogWarning("Repository at {RepositoryLocation} is not a Git repository", config.RepositoryLocation);
                    return Enumerable.Empty<GitTagVersion>();
                }

                using var repo = new Repository(config.RepositoryLocation);
                var tags = repo.Tags
                    .Select(tag => GitTagVersion.TryParse(tag.FriendlyName, out var version) ? version : null)
                    .Where(v => v != null)
                    .Cast<GitTagVersion>()
                    .OrderByDescending(v => v.Version);

                logger.LogDebug("Found {Count} valid version tags in repository", tags.Count());
                return tags;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get versions from repository at {RepositoryLocation}", config.RepositoryLocation);
                return Enumerable.Empty<GitTagVersion>();
            }
        }

        public static bool Pull(this DockerComposeConfiguration config, ILogger logger)
        {
            try
            {
                logger.LogInformation("Pulling latest changes for repository at {RepositoryLocation}", config.RepositoryLocation);

                if (!config.IsGitVersioned)
                {
                    logger.LogError("Repository at {RepositoryLocation} is not a Git repository", config.RepositoryLocation);
                    return false;
                }

                using var repo = new Repository(config.RepositoryLocation);
                var signature = new Signature(config.MergerName, config.MergerEmail, DateTimeOffset.Now);

                var pullOptions = new PullOptions
                {
                    FetchOptions = new FetchOptions(),
                    MergeOptions = new MergeOptions()
                };

                var result = Commands.Pull(repo, signature, pullOptions);
                var success = result.Status != MergeStatus.Conflicts;

                if (success)
                {
                    logger.LogInformation("Repository pulled successfully. Status: {Status}", result.Status);
                }
                else
                {
                    logger.LogError("Pull failed with conflicts. Status: {Status}", result.Status);
                }

                return success;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to pull repository at {RepositoryLocation}", config.RepositoryLocation);
                return false;
            }
        }

        public static void Checkout(this DockerComposeConfiguration config, GitTagVersion version, ILogger logger)
        {
            try
            {
                logger.LogInformation("Checking out version {Version} in repository at {RepositoryLocation}", 
                    version.FriendlyName, config.RepositoryLocation);

                if (!config.IsGitVersioned)
                {
                    throw new InvalidOperationException($"Repository at {config.RepositoryLocation} is not a Git repository");
                }

                using var repo = new Repository(config.RepositoryLocation);
                
                // Find the tag
                var tag = repo.Tags.FirstOrDefault(t => t.FriendlyName == version.FriendlyName);
                if (tag == null)
                {
                    throw new InvalidOperationException($"Tag {version.FriendlyName} not found in repository");
                }

                // Checkout the tag
                Commands.Checkout(repo, tag.Target.Sha);

                logger.LogInformation("Successfully checked out version {Version}", version.FriendlyName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to checkout version {Version} in repository at {RepositoryLocation}", 
                    version.FriendlyName, config.RepositoryLocation);
                throw;
            }
        }
    }
}