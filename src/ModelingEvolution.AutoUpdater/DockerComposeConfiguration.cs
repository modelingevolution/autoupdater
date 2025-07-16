using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Extensions;

namespace ModelingEvolution.AutoUpdater
{
    /// <summary>
    /// Configuration data for Docker Compose deployments - Pure data record
    /// </summary>
    public record DockerComposeConfiguration 
    {
        public string RepositoryLocation { get; init; } = string.Empty;
        public string RepositoryUrl { get; init; } = string.Empty;
        public string DockerComposeDirectory { get; init; } = "./";
        public string? DockerAuth { get; init; }
        public string? DockerRegistryUrl { get; init; }
        public string MergerName { get; init; } = "deploy";
        public string MergerEmail { get; init; } = "deploy@modelingeovlution.com";
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

        // Status properties - require ILogger dependency injection
        private static ILogger? _logger;

        /// <summary>
        /// Sets the logger for status operations. Should be called during DI setup.
        /// </summary>
        public static void SetLogger(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Checks if an upgrade is available for this package
        /// </summary>
        public bool IsUpgradeAvailable => _logger != null && this.IsUpgradeAvailable(_logger);

        /// <summary>
        /// Gets the available upgrade version if one exists
        /// </summary>
        public GitTagVersion? AvailableUpgrade => _logger != null ? this.AvailableUpgrade(_logger) : null;

        /// <summary>
        /// Gets the status text for display purposes
        /// </summary>
        public string StatusText
        {
            get
            {
                if (_logger == null) return "Status unavailable";
                
                if (IsUpgradeAvailable)
                {
                    var upgrade = AvailableUpgrade;
                    return upgrade != null ? $"Upgrade available: {upgrade}" : "Upgrade available";
                }
                return "You have the latest version.";
            }
        }

        /// <summary>
        /// Gets the status color for display purposes
        /// </summary>
        public PackageStatusColor StatusColor
        {
            get
            {
                if (_logger == null) return PackageStatusColor.Warning;
                return IsUpgradeAvailable ? PackageStatusColor.Warning : PackageStatusColor.Success;
            }
        }

        /// <summary>
        /// Error message for this package (managed externally)
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

      
    }

    /// <summary>
    /// Status color enumeration for package display
    /// </summary>
    public enum PackageStatusColor
    {
        Success,   // Green - Up to date
        Warning,   // Orange - Upgrade available
        Error      // Red - Error occurred
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