using ModelingEvolution.AutoUpdater;

namespace ModelingEvolution.AutoUpdater.Host.Services;

/// <summary>
/// Hosted service to initialize static dependencies for DockerComposeConfiguration
/// </summary>
public class PackageStatusInitializationService(DockerComposeConfigurationModel model, UpdateHost uh, ILogger<DockerComposeConfiguration> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(1000, cancellationToken);

        var packages = model.GetPackages();
        for (int i = 0; i < packages.Count; i++)
        {
            var pck = packages[i];
            try
            {
                await uh.CheckIsUpdateAvailable(pck);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize package {PackageName} status", pck.FriendlyName);
            }
        }


    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}