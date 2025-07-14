using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ModelingEvolution.AutoUpdater
{
    /// <summary>
    /// Configuration data for Docker Compose deployments - Pure data record
    /// </summary>
    public record DockerComposeConfiguration : IDisposable
    {
        public string RepositoryLocation { get; init; } = string.Empty;
        public string RepositoryUrl { get; init; } = string.Empty;
        public string DockerComposeDirectory { get; init; } = "./";
        public string? DockerAuth { get; init; }
        public string? DockerRegistryUrl { get; init; }
        public string MergerName { get; init; } = "pi-admin";
        public string MergerEmail { get; init; } = "admin@eventpi.com";
        public IList<DockerRegistryPat> DockerAuths { get; init; } = new List<DockerRegistryPat>();

        public DockerComposeConfiguration(string repositoryLocation, string repositoryUrl,
            string dockerComposeDirectory = "./", string? dockerAuth = null, string? dockerRegistryUrl = null)
        {
            RepositoryLocation = repositoryLocation;
            RepositoryUrl = repositoryUrl;
            DockerComposeDirectory = dockerComposeDirectory;
            DockerAuth = dockerAuth;
            DockerRegistryUrl = dockerRegistryUrl;

            // Add to DockerAuths if provided
            if (!string.IsNullOrEmpty(dockerAuth))
            {
                var registry = dockerRegistryUrl ?? "https://index.docker.io/v1/";
                DockerAuths.Add(new DockerRegistryPat(registry, dockerAuth));
            }
        }

        public DockerComposeConfiguration()
        {
            // Initialize DockerAuths from properties if set
            if (!string.IsNullOrEmpty(DockerAuth))
            {
                var registry = DockerRegistryUrl ?? "https://index.docker.io/v1/";
                DockerAuths.Add(new DockerRegistryPat(registry, DockerAuth));
            }
        }

        // Computed properties - simple data derivations only
        public string ComposeFolderPath => Path.Combine(RepositoryLocation, DockerComposeDirectory);
        public string FriendlyName => Path.GetFileName(RepositoryLocation);
        public bool IsGitVersioned => Directory.Exists(RepositoryLocation) &&
                                      Directory.Exists(Path.Combine(RepositoryLocation, ".git"));

        /// <summary>
        /// Gets the current deployed version from deployment state file
        /// This is kept as a computed property since it's a simple file read
        /// </summary>
        public string? CurrentVersion
        {
            get
            {
                try
                {
                    var stateFile = Path.Combine(ComposeFolderPath, "deployment.state.json");
                    if (!File.Exists(stateFile))
                        return null;

                    var stateContent = File.ReadAllText(stateFile);
                    var state = JsonSerializer.Deserialize<DeploymentState>(stateContent);
                    return state?.Version;
                }
                catch
                {
                    return null;
                }
            }
        }

        public void Dispose()
        {
            // No cleanup needed for this data-only record
        }
    }

    public class UpdateFailedException : Exception
    {
        public UpdateFailedException(string message) : base(message)
        {
        }

        public UpdateFailedException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}