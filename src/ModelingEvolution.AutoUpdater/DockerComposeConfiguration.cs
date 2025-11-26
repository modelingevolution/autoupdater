using System;
using System.Collections.Generic;
using System.IO;

namespace ModelingEvolution.AutoUpdater
{
    /// <summary>
    /// Configuration data for Docker Compose deployments
    /// This class now only contains configuration data, UI state has been moved to PackageState
    /// </summary>
    public class DockerComposeConfiguration
    {
        // This is local repository location, not host repository path.
        public string RepositoryLocation { get; init; } = string.Empty;
        public string RepositoryUrl { get; init; } = string.Empty;
        public string DockerComposeDirectory { get; init; } = "./";
        public string? DockerAuth { get; init; }
        public string? DockerRegistryUrl { get; init; }
        
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
            if (string.IsNullOrEmpty(dockerAuth)) return;
            
            var registry = dockerRegistryUrl ?? "https://index.docker.io/v1/";
            DockerAuths.Add(new DockerRegistryPat(registry, dockerAuth));
        }

        public DockerComposeConfiguration()
        {
            // Initialize DockerAuths from properties if set
            if (string.IsNullOrEmpty(DockerAuth)) return;
            
            var registry = DockerRegistryUrl ?? "https://index.docker.io/v1/";
            DockerAuths.Add(new DockerRegistryPat(registry, DockerAuth));
        }

        public string HostRepositoriesRoot { get; set; } = "/var/docker/configuration";
        
        // Computed properties - simple data derivations only
        public string LocalComposeFolderPath => Path.Combine(RepositoryLocation, DockerComposeDirectory);
        public string HostComposeFolderPath => $"{HostRepositoriesRoot}/{FriendlyName}/{DockerComposeDirectory}";
        public PackageName FriendlyName => Path.GetFileName(RepositoryLocation);
        public bool IsGitVersioned => Directory.Exists(RepositoryLocation) &&
                                      Directory.Exists(Path.Combine(RepositoryLocation, ".git"));
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