using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host.Models;

namespace ModelingEvolution.AutoUpdater.Host.Services;

/// <summary>
/// Hosted service to initialize static dependencies for DockerComposeConfiguration
/// </summary>
/// <remarks>
/// It shows each startup check on <paramref name="readModel"/> before running it. Taking the read model here also creates it,
/// and so subscribes it, before the checks publish their results: a lazily created read model missed them, and every page
/// showed "Version unknown" after a restart until someone pressed refresh.
/// </remarks>
public class PackageStatusInitializationService(DockerComposeConfigurationModel model, UpdateHost uh, PackageStateReadModel readModel, ILogger<DockerComposeConfiguration> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(1000, cancellationToken);

        var packages = model.GetPackages();
        for (int i = 0; i < packages.Count; i++)
        {
            var pck = packages[i];
            readModel.BeginVersionCheck(pck.FriendlyName);
            try
            {
                await uh.CheckIsUpdateAvailable(pck);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize package {PackageName} status", pck.FriendlyName);
                // The check ended without publishing its result: do not leave the package showing "checking" forever.
                readModel.AbandonVersionCheck(pck.FriendlyName);
            }
        }


    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}