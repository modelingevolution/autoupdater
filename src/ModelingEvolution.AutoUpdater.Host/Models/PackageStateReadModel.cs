using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;
using ModelingEvolution.AutoUpdater.Host.Api.AutoUpdater.Models;

namespace ModelingEvolution.AutoUpdater.Host.Models
{
    /// <summary>
    /// Read model that maintains package state based on events
    /// Following CQRS pattern similar to micro-plumberd
    /// </summary>
    public class PackageStateReadModel : IDisposable
    {
        private readonly IEventHub _eventHub;
        private readonly ConcurrentDictionary<PackageName, Item> _packageIndex;
        private readonly ObservableCollection<Item> _packages;
        private SubscriptionSet? _subscriptions;

        /// <summary>
        /// Represents a package item with its state and configuration
        /// </summary>
        public record Item(PackageState State, DockerComposeConfiguration Config);

        /// <summary>
        /// Observable collection of packages for UI binding
        /// </summary>
        public ObservableCollection<Item> Packages => _packages;

        public PackageStateReadModel(IEventHub eventHub, DockerComposeConfigurationModel configModel)
        {
            _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));
            _packageIndex = new ConcurrentDictionary<PackageName, Item>();
            _packages = new ObservableCollection<Item>();

            // Initialize from configuration
            InitializeFromConfiguration(configModel);

            // Subscribe to events
            SubscribeToEvents();
        }

        private void InitializeFromConfiguration(DockerComposeConfigurationModel configModel)
        {
            foreach (var config in configModel.GetPackages())
            {
                var state = new PackageState(config.FriendlyName);
                var item = new Item(state, config);
                
                if (_packageIndex.TryAdd(config.FriendlyName, item))
                {
                    _packages.Add(item);
                }
            }
        }

        private void SubscribeToEvents()
        {
            _subscriptions = _eventHub.Subscribe<VersionCheckCompletedEvent>(Given)
                + _eventHub.Subscribe<UpdateStartedEvent>(Given)
                + _eventHub.Subscribe<UpdateProgressEvent>(Given)
                + _eventHub.Subscribe<UpdateCompletedEvent>(Given)
                + _eventHub.Subscribe<PackageStatusChangedEvent>(Given);
        }

        /// <summary>
        /// Handles VersionCheckCompletedEvent
        /// </summary>
        private void Given(VersionCheckCompletedEvent e)
        {
            if (_packageIndex.TryGetValue(e.ApplicationName, out var item))
            {
                var state = item.State;
                
                // Update state based on event
                state.IsCheckingForUpdates = false;
                state.LastChecked = DateTime.UtcNow;
                
                if (!string.IsNullOrEmpty(e.ErrorMessage))
                {
                    state.OperationMessage = e.ErrorMessage;
                    state.IsPackageValid = false;
                }
                else
                {
                    state.CurrentVersion = !string.IsNullOrEmpty(e.CurrentVersion) && e.CurrentVersion != "-" 
                        ? PackageVersion.Parse(e.CurrentVersion) 
                        : (PackageVersion?)null;
                    
                    state.AvailableVersion = !string.IsNullOrEmpty(e.AvailableVersion) && e.AvailableVersion != "-"
                        ? PackageVersion.Parse(e.AvailableVersion)
                        : (PackageVersion?)null;
                    
                    state.IsUpgradeAvailable = e.IsUpgradeAvailable;
                    state.IsPackageValid = true;
                    state.OperationMessage = string.Empty;
                }
            }
        }

        /// <summary>
        /// Handles UpdateStartedEvent
        /// </summary>
        private void Given(UpdateStartedEvent e)
        {
            if (_packageIndex.TryGetValue(e.ApplicationName, out var item))
            {
                var state = item.State;
                state.OperationMessage = $"Updating from {e.CurrentVersion} to {e.TargetVersion}...";
                state.IsCheckingForUpdates = false;
            }
        }

        /// <summary>
        /// Handles UpdateProgressEvent
        /// </summary>
        private void Given(UpdateProgressEvent e)
        {
            if (_packageIndex.TryGetValue(e.ApplicationName, out var item))
            {
                var state = item.State;
                state.OperationMessage = $"{e.Operation} ({e.ProgressPercentage}%)";
            }
        }

        /// <summary>
        /// Handles UpdateCompletedEvent
        /// </summary>
        private void Given(UpdateCompletedEvent e)
        {
            if (_packageIndex.TryGetValue(e.ApplicationName, out var item))
            {
                var state = item.State;
                
                if (e.Success)
                {
                    state.CurrentVersion = !string.IsNullOrEmpty(e.NewVersion) && e.NewVersion != "-"
                        ? PackageVersion.Parse(e.NewVersion)
                        : (PackageVersion?)null;
                    state.AvailableVersion = null;
                    state.IsUpgradeAvailable = false;
                    state.OperationMessage = "Update completed successfully";
                    state.IsPackageValid = true;
                }
                else
                {
                    state.OperationMessage = e.ErrorMessage ?? "Update failed";
                    state.IsPackageValid = false;
                }
            }
        }

        /// <summary>
        /// Handles PackageStatusChangedEvent
        /// </summary>
        private void Given(PackageStatusChangedEvent e)
        {
            if (_packageIndex.TryGetValue(e.PackageName, out var item))
            {
                var state = item.State;
                state.Status = e.Status;
            }
        }

        /// <summary>
        /// Gets the state for a specific package
        /// </summary>
        public PackageState? GetPackageState(PackageName packageName)
        {
            return _packageIndex.TryGetValue(packageName, out var item) ? item.State : null;
        }

        /// <summary>
        /// Gets the item (state + config) for a specific package
        /// </summary>
        public Item? GetPackageItem(PackageName packageName)
        {
            return _packageIndex.TryGetValue(packageName, out var item) ? item : null;
        }

        /// <summary>
        /// Triggers a version check for a package
        /// </summary>
        public void BeginVersionCheck(PackageName packageName)
        {
            if (_packageIndex.TryGetValue(packageName, out var item))
            {
                item.State.IsCheckingForUpdates = true;
                item.State.OperationMessage = "Checking for updates...";
            }
        }

        public void Dispose()
        {
            _subscriptions?.Dispose();
        }
    }
}