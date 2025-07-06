using Microsoft.Extensions.Logging;

namespace ModelingEvolution.AutoUpdater;

public class UpdateProcessManager(DockerComposeConfigurationRepository repo, UpdateHost host, ILogger<UpdateProcessManager> logger)
{
    private readonly ILogger<UpdateProcessManager> _logger = logger;
    
    public async Task UpdateAll()
    {
        _logger.LogInformation("Starting update process for all packages");
        
        foreach(var i in repo.GetPackages())
        {
            try
            {
                _logger.LogInformation("Updating package: {PackageName}", i.FriendlyName);
                await i.Update(host);
                _logger.LogInformation("Successfully updated package: {PackageName}", i.FriendlyName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update package: {PackageName}", i.FriendlyName);
                throw;
            }
        }
        
        _logger.LogInformation("Update process completed for all packages");
    }
}