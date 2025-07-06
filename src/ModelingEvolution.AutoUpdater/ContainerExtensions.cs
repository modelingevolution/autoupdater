using Microsoft.Extensions.DependencyInjection;

namespace ModelingEvolution.AutoUpdater
{
    public static class ContainerExtensions
    {
        public static IServiceCollection AddAutoUpdater(this IServiceCollection container)
        {
            container.AddSingleton<UpdateProcessManager>();
            container.AddSingleton<UpdateHost>();
            container.AddSingleton<DockerComposeConfigurationRepository>();
            container.AddHostedService(sp => sp.GetRequiredService<UpdateHost>());
            return container;
        }

    }
}
