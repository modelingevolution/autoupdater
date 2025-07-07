using Ductus.FluentDocker.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Collections.ObjectModel;

namespace ModelingEvolution.AutoUpdater
{
    public class DockerComposeConfigurationRepository
    {
        private readonly ObservableCollection<DockerComposeConfiguration> _items = new();
        private readonly IConfiguration configuration;
        private readonly ILogger<DockerComposeConfigurationRepository> _logger;

        public DockerComposeConfigurationRepository(IConfiguration configuration, ILogger<DockerComposeConfigurationRepository> logger)
        {
            this.configuration = configuration;
            _logger = logger;
            ChangeToken.OnChange(() => this.configuration.GetReloadToken(), this.OnConfigurationReloaded);
            OnConfigurationReloaded();
        }

        private void OnConfigurationReloaded()
        {            
            foreach (var i in _items) i.Dispose();
            _items.Clear();

            LoadSection("StdPackages");
            LoadSection("Packages");
            _logger.LogInformation("Configuration reloaded");
        }
        private void LoadSection(string section)
        {
            var items = configuration.GetSection(section).Get<DockerComposeConfiguration[]>();
            if (items != null && items.Any())
                foreach (var i in items) _items.Add(i);
            else _logger.LogInformation("No items in section {section}", section);
        }

        public IReadOnlyList<DockerComposeConfiguration> GetPackages() => _items;

    }
}
