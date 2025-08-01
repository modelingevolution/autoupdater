using ModelingEvolution.AutoUpdater.Host.Services.VPN;
using ModelingEvolution.AutoUpdater.Host.Services;
using ModelingEvolution.AutoUpdater.Services;
using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Extensions;
using ModelingEvolution.AutoUpdater.Host.Api.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host.Models;

namespace ModelingEvolution.AutoUpdater.Host.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAutoUpdaterHost(this IServiceCollection services)
    {
        services.AddSingleton<AutoUpdaterService>();
        services.AddSingleton<IBrandingService, BrandingService>();
        
        // Configure in-memory logging
        services.AddLogging(builder => builder.AddInMemoryLogging());
        
        // Configure DockerComposeConfiguration with logger through a hosted service
        services.AddHostedService<PackageStatusInitializationService>();

        // Register PackageStateReadModel as singleton
        services.AddSingleton<PackageStateReadModel>();

        // Register SSH VPN service using extension methods
        services.AddSingleton<ISshVpnService>(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var vpnProviderAccess = configuration.VpnProviderAccess();
            
            
            if (string.IsNullOrWhiteSpace(vpnProviderAccess) || vpnProviderAccess == "None" )
            {
                return new DisabledSshVpnService();
            }
            
            return new SshVpnService(
                provider.GetRequiredService<ILogger<SshVpnService>>(),
                configuration, provider.GetRequiredService<ISshConnectionManager>()
            );
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