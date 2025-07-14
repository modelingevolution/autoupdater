using ModelingEvolution.AutoUpdater;

namespace ModelingEvolution.AutoUpdater.Host.Services;

/// <summary>
/// Hosted service to initialize static dependencies for DockerComposeConfiguration
/// </summary>
public class PackageStatusInitializationService : IHostedService
{
    private readonly ILogger<DockerComposeConfiguration> _logger;

    public PackageStatusInitializationService(ILogger<DockerComposeConfiguration> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Set the static logger for DockerComposeConfiguration status operations
        DockerComposeConfiguration.SetLogger(_logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}