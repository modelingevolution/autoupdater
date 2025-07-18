using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Extensions;
using ModelingEvolution.AutoUpdater.Services;

namespace ModelingEvolution.AutoUpdater
{
    /// <summary>
    /// Configuration data for Docker Compose deployments with observable properties for UI binding
    /// </summary>
    public class DockerComposeConfiguration : INotifyPropertyChanged
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

       

       
        private string _operationMessage = string.Empty;
        private string? _availableUpgrade;
        private bool _isUpgradeAvailable;
        private bool _isPackageValid;

        /// <summary>
        /// message for this package (managed externally)
        /// </summary>
        public string OperationMessage 
        { 
            get => _operationMessage;
            set => SetProperty(ref _operationMessage, value);
        }

        /// <summary>
        /// Available upgrade version
        /// </summary>
        public string? AvailableUpgrade
        {
            get => _availableUpgrade;
            set => SetProperty(ref _availableUpgrade, value);
        }

        /// <summary>
        /// Whether an upgrade is available
        /// </summary>
        public bool IsUpgradeAvailable
        {
            get => _isUpgradeAvailable;
            set => SetProperty(ref _isUpgradeAvailable, value);
        }

        public bool IsPackageValid
        {
            get => _isPackageValid;
            set => SetProperty(ref _isPackageValid, value);
        }

        /// <summary>
        /// Gets the status text for display purposes
        /// </summary>
        public string StatusText
        {
            get
            {
                if (IsUpgradeAvailable)
                {
                    return AvailableUpgrade != null ? $"Upgrade available: {AvailableUpgrade}" : "Upgrade available";
                }
                if(IsPackageValid)
                    return "You have the latest version.";
                return OperationMessage ?? "Error";
            }
        }

        /// <summary>
        /// Gets the status color for display purposes
        /// </summary>
        public PackageStatusColor StatusColor
        {
            get
            {
                if (!string.IsNullOrEmpty(OperationMessage))
                    return PackageStatusColor.Error;
                    
                return IsUpgradeAvailable ? PackageStatusColor.Warning : PackageStatusColor.Success;
            }
        }

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
                
            field = value;
            OnPropertyChanged(propertyName);
            
            // Notify dependent properties
            if (propertyName == nameof(IsUpgradeAvailable) || propertyName == nameof(AvailableUpgrade))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
            }
            else if (propertyName == nameof(OperationMessage))
            {
                OnPropertyChanged(nameof(StatusColor));
            }
            
            return true;
        }

        #endregion
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