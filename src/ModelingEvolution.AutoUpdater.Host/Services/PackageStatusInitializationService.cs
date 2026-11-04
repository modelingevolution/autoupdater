using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host.Models;

namespace ModelingEvolution.AutoUpdater.Host.Services;

/// <summary>
/// Hosted service to initialize static dependencies for DockerComposeConfiguration
/// </summary>
/// <remarks>
/// It takes <paramref name="readModel"/> only so the read model exists, and has subscribed, before the startup version
/// checks publish their results. A lazily created read model missed them, and every page showed "Version unknown" after a
/// restart until someone pressed refresh.
/// </remarks>
#pragma warning disable CS9113 // The parameter is unread on purpose: see remarks.
public class PackageStatusInitializationService(DockerComposeConfigurationModel model, UpdateHost uh, PackageStateReadModel readModel, ILogger<DockerComposeConfiguration> logger) : IHostedService
#pragma warning restore CS9113
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