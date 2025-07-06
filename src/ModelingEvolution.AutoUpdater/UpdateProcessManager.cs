using Microsoft.Extensions.Logging;

namespace ModelingEvolution.AutoUpdater;

public class UpdateProcessManager(DockerComposeConfigurationRepository repo, UpdateHost host, ILogger<UpdateProcessManager> logger)
{        
    public async Task UpdateAll()
    {
        foreach(var i in repo.GetPackages())
        {
            await i.Update(host);
        }
    }
}