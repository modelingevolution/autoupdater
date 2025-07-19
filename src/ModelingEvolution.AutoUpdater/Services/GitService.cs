using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
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
        private static readonly IReadOnlyList<PackageVersion> EmptyVersionList = new List<PackageVersion>(0).AsReadOnly();

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

        public async Task<IReadOnlyList<PackageVersion>> GetAvailableVersionsAsync(string repositoryPath)
        {
            try
            {
                _logger.LogDebug("Getting available versions from repository {RepositoryPath}", repositoryPath);

                if (!IsGitRepository(repositoryPath))
                {
                    _logger.LogWarning("Path {RepositoryPath} is not a Git repository", repositoryPath);
                    return EmptyVersionList;
                }

                return await Task.Run(() =>
                {
                    using var repo = new Repository(repositoryPath);
                    
                    // Pre-size list to avoid reallocations - most repos have < 50 tags
                    var versions = new List<PackageVersion>(Math.Min(repo.Tags.Count(), 100));
                    
                    // Parse versions directly into the list
                    foreach (var tag in repo.Tags)
                    {
                        if (PackageVersion.TryParse(tag.FriendlyName, out var version))
                        {
                            versions.Add(version);
                        }
                    }
                    
                    // Sort in-place to avoid creating intermediate collections
                    versions.Sort((x, y) => y.CompareTo(x));
                    
                    return versions;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get available versions from {RepositoryPath}", repositoryPath);
                return EmptyVersionList;
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

                    if (origin == null) return;
                    
                    var refSpecs = origin.FetchRefSpecs.Select(spec => spec.Specification);
                    var fetchOptions = new FetchOptions
                    {
                        TagFetchMode = TagFetchMode.All
                    };

                    Commands.Fetch(repo, "origin", refSpecs, fetchOptions, null);
                });

                _logger.LogInformation("Successfully fetched latest tags and references");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch from {RepositoryPath}", repositoryPath);
                throw;
            }
        }

        public async Task<bool> InitializeRepositoryAsync(string repositoryPath, string remoteUrl)
        {
            try
            {
                _logger.LogInformation("Initializing Git repository at {RepositoryPath} with remote {RemoteUrl}", repositoryPath, remoteUrl);

                if (!Directory.Exists(repositoryPath))
                {
                    _logger.LogError("Directory {RepositoryPath} does not exist", repositoryPath);
                    return false;
                }

                if (IsGitRepository(repositoryPath))
                {
                    _logger.LogWarning("Directory {RepositoryPath} is already a Git repository", repositoryPath);
                    return false;
                }

                await Task.Run(() =>
                {
                    // Initialize the repository
                    Repository.Init(repositoryPath);
                    
                    using var repo = new Repository(repositoryPath);
                    
                    // Add remote origin
                    repo.Network.Remotes.Add("origin", remoteUrl);
                    
                    // Fetch from remote to get all branches and tags
                    var fetchOptions = new FetchOptions
                    {
                        TagFetchMode = TagFetchMode.All
                    };
                    
                    var refSpecs = repo.Network.Remotes["origin"].FetchRefSpecs.Select(spec => spec.Specification);
                    Commands.Fetch(repo, "origin", refSpecs, fetchOptions, null);
                    
                    // Set up tracking branch for master/main
                    var remoteBranches = repo.Branches.Where(b => b.IsRemote && 
                        (b.FriendlyName.EndsWith("/master") || b.FriendlyName.EndsWith("/main"))).ToList();
                    
                    if (remoteBranches.Any())
                    {
                        var mainBranch = remoteBranches.First();
                        var localBranchName = mainBranch.FriendlyName.Split('/').Last();
                        
                        // Create local tracking branch
                        var localBranch = repo.CreateBranch(localBranchName, mainBranch.Tip);
                        repo.Branches.Update(localBranch, b => b.TrackedBranch = mainBranch.CanonicalName);
                        
                        // Checkout the local branch
                        Commands.Checkout(repo, localBranch);
                    }
                });

                _logger.LogInformation("Successfully initialized Git repository at {RepositoryPath}", repositoryPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Git repository at {RepositoryPath}", repositoryPath);
                return false;
            }
        }
    }
}