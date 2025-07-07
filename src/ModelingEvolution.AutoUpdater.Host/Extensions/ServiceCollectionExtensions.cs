using ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater;

namespace ModelingEvolution.AutoUpdater.Host.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<AutoUpdaterService>(sp =>
        {
            var repo = sp.GetRequiredService<DockerComposeConfigurationRepository>();
            var updateManager = sp.GetRequiredService<UpdateProcessManager>();
            var updateHost = sp.GetRequiredService<UpdateHost>();
            var logger = sp.GetRequiredService<ILogger<AutoUpdaterService>>();
            return new AutoUpdaterService(repo, updateManager, updateHost, logger);
        });
        return services;
    }

    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        // Enable API Explorer for Minimal APIs
        services.AddEndpointsApiExplorer();
        
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        return services;
    }
}