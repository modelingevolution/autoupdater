using ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host.Services.VPN;
using ModelingEvolution.AutoUpdater.Host.Services;
using ModelingEvolution.AutoUpdater.Services;
using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Extensions;

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

        // Register SSH VPN service using extension methods
        services.AddSingleton<ISshVpnService>(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var vpnProviderAccess = configuration.VpnProviderAccess();
            var vpnProvider = configuration.VpnProvider();
            
            if (vpnProviderAccess != "Ssh" || vpnProvider != "Wireguard")
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