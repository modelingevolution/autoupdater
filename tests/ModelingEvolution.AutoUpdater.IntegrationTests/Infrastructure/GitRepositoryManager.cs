using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;

/// <summary>
/// Manages Git repositories for integration testing
/// </summary>
public class GitRepositoryManager : IDisposable
{
    private readonly ILogger<GitRepositoryManager> _logger;
    private readonly List<string> _createdRepositories = new();

    public GitRepositoryManager(ILogger<GitRepositoryManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Clones a repository to a local directory
    /// </summary>
    public async Task<string> CloneRepositoryAsync(
        string repositoryUrl,
        string? targetDirectory = null,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        targetDirectory ??= Path.Combine(Path.GetTempPath(), $"git-repo-{Guid.NewGuid():N}");
        
        _logger.LogInformation("Cloning repository {Url} to {Directory}", repositoryUrl, targetDirectory);

        await Task.Run(() =>
        {
            var cloneOptions = new CloneOptions();
            if (!string.IsNullOrEmpty(branch))
            {
                cloneOptions.BranchName = branch;
            }

            Repository.Clone(repositoryUrl, targetDirectory, cloneOptions);
        }, cancellationToken);

        _createdRepositories.Add(targetDirectory);
        _logger.LogInformation("Successfully cloned repository to {Directory}", targetDirectory);
        
        return targetDirectory;
    }

    /// <summary>
    /// Creates a new Git repository in the specified directory
    /// </summary>
    public string CreateRepository(string directory, bool bare = false)
    {
        _logger.LogInformation("Creating Git repository in {Directory}", directory);

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Repository.Init(directory, bare);
        _createdRepositories.Add(directory);
        
        _logger.LogInformation("Successfully created Git repository in {Directory}", directory);
        return directory;
    }

    /// <summary>
    /// Commits changes to a repository
    /// </summary>
    public void CommitChanges(string repositoryPath, string message, string authorName = "Test User", string authorEmail = "test@example.com")
    {
        _logger.LogInformation("Committing changes to repository {Path}", repositoryPath);

        using var repo = new Repository(repositoryPath);
        
        // Stage all changes
        Commands.Stage(repo, "*");

        // Create signature
        var signature = new Signature(authorName, authorEmail, DateTimeOffset.Now);

        // Commit
        var commit = repo.Commit(message, signature, signature);
        
        _logger.LogInformation("Successfully committed changes with commit {CommitId}", commit.Id.Sha[..7]);
    }

    /// <summary>
    /// Creates and pushes a new tag
    /// </summary>
    public void CreateTag(string repositoryPath, string tagName, string? message = null)
    {
        _logger.LogInformation("Creating tag {TagName} in repository {Path}", tagName, repositoryPath);

        using var repo = new Repository(repositoryPath);
        
        var signature = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
        repo.Tags.Add(tagName, repo.Head.Tip, signature, message ?? $"Tag {tagName}");
        
        _logger.LogInformation("Successfully created tag {TagName}", tagName);
    }

    /// <summary>
    /// Checks out a specific tag or branch
    /// </summary>
    public void Checkout(string repositoryPath, string reference)
    {
        _logger.LogInformation("Checking out {Reference} in repository {Path}", reference, repositoryPath);

        using var repo = new Repository(repositoryPath);
        Commands.Checkout(repo, reference);
        
        _logger.LogInformation("Successfully checked out {Reference}", reference);
    }

    /// <summary>
    /// Gets the current commit SHA
    /// </summary>
    public string GetCurrentCommitSha(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.Head.Tip.Id.Sha;
    }

    /// <summary>
    /// Gets all tags in the repository
    /// </summary>
    public IEnumerable<string> GetTags(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.Tags.Select(t => t.FriendlyName).ToList();
    }

    /// <summary>
    /// Checks if the repository has uncommitted changes
    /// </summary>
    public bool HasUncommittedChanges(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.RetrieveStatus().IsDirty;
    }

    /// <summary>
    /// Gets the current branch name
    /// </summary>
    public string GetCurrentBranch(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.Head.FriendlyName;
    }

    /// <summary>
    /// Creates a new branch
    /// </summary>
    public void CreateBranch(string repositoryPath, string branchName, bool checkout = false)
    {
        _logger.LogInformation("Creating branch {BranchName} in repository {Path}", branchName, repositoryPath);

        using var repo = new Repository(repositoryPath);
        var branch = repo.CreateBranch(branchName);
        
        if (checkout)
        {
            Commands.Checkout(repo, branch);
        }
        
        _logger.LogInformation("Successfully created branch {BranchName}", branchName);
    }

    /// <summary>
    /// Writes content to a file in the repository
    /// </summary>
    public async Task WriteFileAsync(string repositoryPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(repositoryPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, content);
        _logger.LogInformation("Written file {RelativePath} to repository {Path}", relativePath, repositoryPath);
    }

    /// <summary>
    /// Simulates building and tagging a new version of the test application
    /// </summary>
    public async Task<string> PrepareVersionedAppAsync(
        string appRepositoryPath,
        string version,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Preparing versioned app {Version} in {Path}", version, appRepositoryPath);

        // Update version in the application
        var programCsPath = Path.Combine(appRepositoryPath, "Program.cs");
        if (File.Exists(programCsPath))
        {
            var content = await File.ReadAllTextAsync(programCsPath, cancellationToken);
            // Simple version replacement - in real scenario this would be more sophisticated
            content = content.Replace("\"1.0.0\"", $"\"{version}\"");
            await File.WriteAllTextAsync(programCsPath, content, cancellationToken);
        }

        // Commit changes
        CommitChanges(appRepositoryPath, $"Update version to {version}");

        // Create tag
        CreateTag(appRepositoryPath, $"v{version}", $"Release version {version}");

        _logger.LogInformation("Successfully prepared versioned app {Version}", version);
        return version;
    }

    /// <summary>
    /// Prepares versioned compose configuration
    /// </summary>
    public async Task<string> PrepareVersionedComposeAsync(
        string composeRepositoryPath,
        string appVersion,
        string composeVersion,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Preparing compose version {ComposeVersion} for app version {AppVersion}", 
            composeVersion, appVersion);

        // Update docker-compose.yml with new image version
        var composePath = Path.Combine(composeRepositoryPath, "docker-compose.yml");
        if (File.Exists(composePath))
        {
            var content = await File.ReadAllTextAsync(composePath, cancellationToken);
            // Replace image tag with new version
            content = content.Replace("versionapp:latest", $"versionapp:{appVersion}");
            await File.WriteAllTextAsync(composePath, content, cancellationToken);
        }

        // Commit changes
        CommitChanges(composeRepositoryPath, $"Update to app version {appVersion}");

        // Create tag (compose version follows v0.X.Y pattern for app v1.X.Y)
        CreateTag(composeRepositoryPath, $"v{composeVersion}", $"Compose version {composeVersion} for app {appVersion}");

        _logger.LogInformation("Successfully prepared compose version {ComposeVersion}", composeVersion);
        return composeVersion;
    }

    /// <summary>
    /// Cleans up all created repositories
    /// </summary>
    public async Task CleanupAsync()
    {
        if (_createdRepositories.Count == 0)
        {
            _logger.LogInformation("No Git repositories to clean up");
            return;
        }

        _logger.LogInformation("Cleaning up {Count} Git repositories", _createdRepositories.Count);

        var tasks = _createdRepositories.ToList().Select(async repo =>
        {
            try
            {
                if (Directory.Exists(repo))
                {
                    await Task.Run(() =>
                    {
                        // Force delete read-only files that Git creates
                        var di = new DirectoryInfo(repo);
                        SetAttributesNormal(di);
                        di.Delete(true);
                    });
                    _logger.LogInformation("Cleaned up repository: {Repo}", repo);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up repository {Repo}", repo);
            }
        });

        await Task.WhenAll(tasks);
        _createdRepositories.Clear();
    }

    /// <summary>
    /// Recursively removes read-only attributes from files and directories
    /// </summary>
    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
        }
        
        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
        
        dir.Attributes = FileAttributes.Normal;
    }

    public void Dispose()
    {
        // Cleanup is async, so we can't do it in Dispose
        // Users should call CleanupAsync() explicitly
    }
}