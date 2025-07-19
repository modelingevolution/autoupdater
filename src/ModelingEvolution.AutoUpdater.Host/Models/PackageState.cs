using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ModelingEvolution.AutoUpdater.Common;

namespace ModelingEvolution.AutoUpdater.Host.Models
{
    /// <summary>
    /// Represents the UI state for a package, separate from its configuration
    /// </summary>
    public class PackageState : INotifyPropertyChanged
    {
        private string _operationMessage = string.Empty;
        private PackageVersion? _currentVersion;
        private PackageVersion? _availableVersion;
        private bool _isUpgradeAvailable;
        private bool _isPackageValid = true;
        private bool _isCheckingForUpdates;
        private bool _isUpdateInProgress;
        private DateTime? _lastChecked;
        private string _status = "unknown";

        /// <summary>
        /// The package name this state belongs to
        /// </summary>
        public PackageName PackageName { get; }

        /// <summary>
        /// Operation message for this package (error messages, status updates, etc.)
        /// </summary>
        public string OperationMessage
        {
            get => _operationMessage;
            set => SetProperty(ref _operationMessage, value);
        }

        /// <summary>
        /// Current installed version of the package
        /// </summary>
        public PackageVersion? CurrentVersion
        {
            get => _currentVersion;
            set => SetProperty(ref _currentVersion, value);
        }

        /// <summary>
        /// Available upgrade version
        /// </summary>
        public PackageVersion? AvailableVersion
        {
            get => _availableVersion;
            set => SetProperty(ref _availableVersion, value);
        }

        /// <summary>
        /// Whether an upgrade is available
        /// </summary>
        public bool IsUpgradeAvailable
        {
            get => _isUpgradeAvailable;
            set => SetProperty(ref _isUpgradeAvailable, value);
        }

        /// <summary>
        /// Whether the package configuration is valid
        /// </summary>
        public bool IsPackageValid
        {
            get => _isPackageValid;
            set => SetProperty(ref _isPackageValid, value);
        }

        /// <summary>
        /// Whether the package is currently being checked for updates
        /// </summary>
        public bool IsCheckingForUpdates
        {
            get => _isCheckingForUpdates;
            set => SetProperty(ref _isCheckingForUpdates, value);
        }

        /// <summary>
        /// Whether the package is currently being updated
        /// </summary>
        public bool IsUpdateInProgress
        {
            get => _isUpdateInProgress;
            set => SetProperty(ref _isUpdateInProgress, value);
        }

        /// <summary>
        /// Last time the package was checked for updates
        /// </summary>
        public DateTime? LastChecked
        {
            get => _lastChecked;
            set => SetProperty(ref _lastChecked, value);
        }

        /// <summary>
        /// Docker compose service status (running, stopped, etc.)
        /// </summary>
        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        /// <summary>
        /// Gets the status text for display purposes
        /// </summary>
        public string StatusText
        {
            get
            {
                if (!IsPackageValid)
                    return OperationMessage ?? "Configuration error";

                if (IsUpdateInProgress)
                    return OperationMessage ?? "Update in progress...";

                if (IsCheckingForUpdates)
                    return "Checking for updates...";

                if (IsUpgradeAvailable && AvailableVersion != null)
                    return $"Upgrade available: {AvailableVersion}";

                if (CurrentVersion != null)
                    return $"Current version: {CurrentVersion}";

                return "Version unknown";
            }
        }

        /// <summary>
        /// Gets the status color for display purposes
        /// </summary>
        public PackageStatusColor StatusColor
        {
            get
            {
                if (!IsPackageValid)
                    return PackageStatusColor.Error;

                if (IsUpdateInProgress || IsCheckingForUpdates)
                    return PackageStatusColor.Info;

                return IsUpgradeAvailable ? PackageStatusColor.Warning : PackageStatusColor.Success;
            }
        }

        public PackageState(PackageName packageName)
        {
            PackageName = packageName;
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
            if (propertyName == nameof(IsUpgradeAvailable) || 
                propertyName == nameof(AvailableVersion) ||
                propertyName == nameof(CurrentVersion) ||
                propertyName == nameof(IsCheckingForUpdates) ||
                propertyName == nameof(IsUpdateInProgress) ||
                propertyName == nameof(IsPackageValid) ||
                propertyName == nameof(OperationMessage))
            {
                OnPropertyChanged(nameof(StatusText));
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
        Error,     // Red - Error occurred
        Info       // Blue - Checking for updates
    }
}