using Ductus.FluentDocker.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace ModelingEvolution.AutoUpdater
{
    
    /// <summary>
    /// 
    /// </summary>
    public class DockerComposeConfigurationModel : IDisposable
    {
        private readonly ObservableCollection<DockerComposeConfiguration> _items = new();
        private readonly ConcurrentDictionary<PackageName, DockerComposeConfiguration> _index = new();
        private readonly IConfiguration _configuration;
        private readonly ILogger<DockerComposeConfigurationModel> _logger;
        private readonly IEventHub _eventHub;
        private readonly SubscriptionSet _subscriptions;

        public DockerComposeConfigurationModel(IConfiguration configuration, ILogger<DockerComposeConfigurationModel> logger, IEventHub eventHub)
        {
            this._configuration = configuration;
            _logger = logger;
            _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));

            // Subscribe to events using SubscriptionSet
            _subscriptions = _eventHub.Subscribe<UpdateStartedEvent>(Given) + 
                             _eventHub.Subscribe<UpdateCompletedEvent>(Given) + 
                             _eventHub.Subscribe<UpdateProgressEvent>(Given) +
                             _eventHub.Subscribe<VersionCheckCompletedEvent>(Given);

            //ChangeToken.OnChange(() => this._configuration.GetReloadToken(), this.OnConfigurationReloaded);
            OnConfigurationReloaded();
        }

        private void OnConfigurationReloaded()
        {
            try
            {
                _items.Clear();
                _index.Clear();

                LoadSection("StdPackages");
                LoadSection("Packages");
                _logger.LogInformation("Configuration loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reloading configuration");
            }
        }
        private void LoadSection(string section)
        {
            var items = _configuration.GetSection(section).Get<DockerComposeConfiguration[]>();
            if (items != null && items.Any())
            {
                foreach (var i in items)
                {
                    _items.Add(i);
                    _index[i.FriendlyName] = i;
                }
            }
            else _logger.LogInformation("No items in section {section}", section);
        }

        public IReadOnlyList<DockerComposeConfiguration> GetPackages() => _items;
        
        public DockerComposeConfiguration? GetPackage(PackageName packageName)
        {
            return _index.GetValueOrDefault(packageName);
        }

        private async Task Given(UpdateStartedEvent updateStarted)
        {
            _logger.LogInformation("Update started for {ApplicationName}: {CurrentVersion} -> {TargetVersion}", 
                updateStarted.ApplicationName, updateStarted.CurrentVersion, updateStarted.TargetVersion);
            
            if (_index.TryGetValue(updateStarted.ApplicationName, out var config))
            {
                config.OperationMessage = "Update in progress...";
                config.IsUpgradeAvailable = false; // Clear upgrade status during update
                _logger.LogDebug("Updated status for {ApplicationName}", updateStarted.ApplicationName);
            }
            
            await Task.CompletedTask;
        }

        private async Task Given(UpdateCompletedEvent updateCompleted)
        {
            _logger.LogInformation("Update completed for {ApplicationName}: Success={Success}, Error={Error}", 
                updateCompleted.ApplicationName, updateCompleted.Success, updateCompleted.ErrorMessage);
            
            if (_index.TryGetValue(updateCompleted.ApplicationName, out var config))
            {
                if (updateCompleted.Success)
                {
                    config.OperationMessage = string.Empty;
                    config.IsUpgradeAvailable = false; // No upgrade available after successful update
                    config.AvailableUpgrade = null;
                    _logger.LogDebug("Cleared error status for {ApplicationName}", updateCompleted.ApplicationName);
                }
                else
                {
                    config.OperationMessage = updateCompleted.ErrorMessage ?? "Update failed";
                    _logger.LogDebug("Set error status for {ApplicationName}: {Error}", 
                        updateCompleted.ApplicationName, config.OperationMessage);
                }
            }
            
            await Task.CompletedTask;
        }

        private async Task Given(UpdateProgressEvent updateProgress)
        {
            _logger.LogDebug("Update progress for {ApplicationName}: {Operation} - {Progress}%", 
                updateProgress.ApplicationName, updateProgress.Operation, updateProgress.ProgressPercentage);
            
            if (_index.TryGetValue(updateProgress.ApplicationName, out var config))
            {
                config.OperationMessage = $"{updateProgress.Operation} ({updateProgress.ProgressPercentage}%)";
            }
            
            await Task.CompletedTask;
        }

        private async Task Given(VersionCheckCompletedEvent versionCheck)
        {
            _logger.LogDebug("Version check completed for {ApplicationName}: Current={CurrentVersion}, Available={AvailableVersion}, UpgradeAvailable={IsUpgradeAvailable}", 
                versionCheck.ApplicationName, versionCheck.CurrentVersion, versionCheck.AvailableVersion, versionCheck.IsUpgradeAvailable);
            
            if (_index.TryGetValue(versionCheck.ApplicationName, out var config))
            {
                if (!string.IsNullOrEmpty(versionCheck.ErrorMessage))
                {
                    config.OperationMessage = versionCheck.ErrorMessage;
                    config.IsUpgradeAvailable = false;
                    config.AvailableUpgrade = null;
                }
                else
                {
                    config.IsUpgradeAvailable = versionCheck.IsUpgradeAvailable;
                    config.AvailableUpgrade = versionCheck.AvailableVersion;
                    config.OperationMessage = string.Empty; // Clear any previous errors
                }
                
                _logger.LogDebug("Updated version status for {ApplicationName}: UpgradeAvailable={IsUpgradeAvailable}, AvailableVersion={AvailableVersion}", 
                    versionCheck.ApplicationName, config.IsUpgradeAvailable, config.AvailableUpgrade);
            }
            
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            _subscriptions?.Dispose();
        }

    }
}
