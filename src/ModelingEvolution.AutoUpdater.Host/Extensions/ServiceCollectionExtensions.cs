using ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host.Services.VPN;
using ModelingEvolution.AutoUpdater.Services;

namespace ModelingEvolution.AutoUpdater.Host.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<AutoUpdaterService>();

        // Register SSH VPN service
        services.AddSingleton<ISshVpnService>(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var vpnProviderAccess = configuration.GetValue<string>("VpnProviderAccess", "None");
            var vpnProvider = configuration.GetValue<string>("VpnProvider", "None");
            
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