using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Implementation of Git operations service
    /// </summary>
    public class GitService : IGitService
    {
        private readonly ILogger<GitService> _logger;

        public GitService(ILogger<GitService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> CloneRepositoryAsync(string repositoryUrl, string targetPath)
        {
            try
            {
                _logger.LogInformation("Cloning repository {RepositoryUrl} to {TargetPath}", repositoryUrl, targetPath);

                if (Directory.Exists(targetPath))
                {
                    _logger.LogWarning("Target directory {TargetPath} already exists", targetPath);
                    return false;
                }

                Directory.CreateDirectory(targetPath);

                var cloneOptions = new CloneOptions
                {
                    FetchOptions = { TagFetchMode = TagFetchMode.All }
                };

                await Task.Run(() => Repository.Clone(repositoryUrl, targetPath, cloneOptions));

                _logger.LogInformation("Successfully cloned repository to {TargetPath}", targetPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clone repository {RepositoryUrl} to {TargetPath}", repositoryUrl, targetPath);
                return false;
            }
        }

        public async Task<bool> PullLatestAsync(string repositoryPath)
        {
            try
            {
                _logger.LogInformation("Pulling latest changes for repository at {RepositoryPath}", repositoryPath);

                if (!IsGitRepository(repositoryPath))
                {
                    _logger.LogError("Path {RepositoryPath} is not a Git repository", repositoryPath);
                    return false;
                }

                await Task.Run(() =>
                {
                    using var repo = new Repository(repositoryPath);
                    var signature = new Signature("AutoUpdater", "autoupdater@modelingevolution.com", DateTimeOffset.Now);

                    var pullOptions = new PullOptions
                    {
                        FetchOptions = new FetchOptions(),
                        MergeOptions = new MergeOptions()
                    };

                    var result = Commands.Pull(repo, signature, pullOptions);
                    return result.Status != MergeStatus.UpToDate;
                });

                _logger.LogInformation("Successfully pulled latest changes for {RepositoryPath}", repositoryPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pull latest changes for {RepositoryPath}", repositoryPath);
                return false;
            }
        }

        public async Task CheckoutVersionAsync(string repositoryPath, string version)
        {
            try
            {
                _logger.LogInformation("Checking out version {Version} in repository {RepositoryPath}", version, repositoryPath);

                if (!IsGitRepository(repositoryPath))
                {
                    throw new InvalidOperationException($"Path {repositoryPath} is not a Git repository");
                }

                await Task.Run(() =>
                {
                    using var repo = new Repository(repositoryPath);
                    
                    // Try to find the tag
                    var tag = repo.Tags[version] ?? repo.Tags[$"v{version}"];
                    if (tag == null)
                    {
                        throw new InvalidOperationException($"Tag {version} not found in repository");
                    }

                    var checkoutOptions = new CheckoutOptions
                    {
                        CheckoutModifiers = CheckoutModifiers.None,
                        CheckoutNotifyFlags = CheckoutNotifyFlags.None
                    };

                    Commands.Checkout(repo, tag.Target.Sha, checkoutOptions);
                });

                _logger.LogInformation("Successfully checked out version {Version}", version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to checkout version {Version} in {RepositoryPath}", version, repositoryPath);
                throw;
            }
        }

        public async Task<IEnumerable<GitTagVersion>> GetAvailableVersionsAsync(string repositoryPath)
        {
            try
            {
                _logger.LogDebug("Getting available versions from repository {RepositoryPath}", repositoryPath);

                if (!IsGitRepository(repositoryPath))
                {
                    _logger.LogWarning("Path {RepositoryPath} is not a Git repository", repositoryPath);
                    return Enumerable.Empty<GitTagVersion>();
                }

                return await Task.Run(() =>
                {
                    using var repo = new Repository(repositoryPath);
                    
                    var versions = new List<GitTagVersion>();
                    foreach (var tag in repo.Tags)
                    {
                        if (GitTagVersion.TryParse(tag.FriendlyName, out var version))
                        {
                            versions.Add(version);
                        }
                    }

                    return versions.OrderByDescending(v => v.Version);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get available versions from {RepositoryPath}", repositoryPath);
                return Enumerable.Empty<GitTagVersion>();
            }
        }


        public bool IsGitRepository(string path)
        {
            try
            {
                return Directory.Exists(path) && Directory.Exists(Path.Combine(path, ".git"));
            }
            catch
            {
                return false;
            }
        }

        public async Task FetchAsync(string repositoryPath)
        {
            try
            {
                _logger.LogInformation("Fetching latest tags and references from {RepositoryPath}", repositoryPath);

                if (!IsGitRepository(repositoryPath))
                {
                    throw new InvalidOperationException($"Path {repositoryPath} is not a Git repository");
                }

                await Task.Run(() =>
                {
                    using var repo = new Repository(repositoryPath);
                    var origin = repo.Network.Remotes["origin"];
                    
                    if (origin != null)
                    {
                        var refSpecs = origin.FetchRefSpecs.Select(spec => spec.Specification);
                        var fetchOptions = new FetchOptions
                        {
                            TagFetchMode = TagFetchMode.All
                        };

                        Commands.Fetch(repo, "origin", refSpecs, fetchOptions, null);
                    }
                });

                _logger.LogInformation("Successfully fetched latest tags and references");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch from {RepositoryPath}", repositoryPath);
                throw;
            }
        }
    }
}