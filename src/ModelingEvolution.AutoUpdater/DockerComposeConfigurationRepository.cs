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

        public DockerComposeConfigurationRepository(IConfiguration configuration, ILogger<DockerComposeConfigurationRepository> logger)
        {
            this.configuration = configuration;
            ChangeToken.OnChange(() => this.configuration.GetReloadToken(), () => { this.OnConfigurationReloaded(); logger.LogInformation("Configuration reloaded"); });
            OnConfigurationReloaded();
        }

        private void OnConfigurationReloaded()
        {            
            foreach (var i in _items) i.Dispose();
            _items.Clear();

            LoadSection("StdPackages");
            LoadSection("Packages");
        }
        private void LoadSection(string section)
        {
            var items = configuration.GetSection(section).Get<DockerComposeConfiguration[]>();
            if (items != null)
                foreach (var i in items) _items.Add(i);
        }

        public IReadOnlyList<DockerComposeConfiguration> GetPackages() => _items;

    }
}
