using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
using System.Collections.Concurrent;
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

        public DockerComposeConfigurationModel(IConfiguration configuration, ILogger<DockerComposeConfigurationModel> logger)
        {
            this._configuration = configuration;
            _logger = logger;

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

        public void Dispose()
        {
            // No longer managing event subscriptions
        }

    }
}
