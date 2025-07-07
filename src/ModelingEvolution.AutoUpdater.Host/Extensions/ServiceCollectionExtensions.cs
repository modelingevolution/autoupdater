using ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater;

namespace ModelingEvolution.AutoUpdater.Host.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<AutoUpdaterService>();
        return services;
    }

    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        // Note: Add Swagger NuGet package to enable OpenAPI documentation
        // services.AddEndpointsApiExplorer();
        // services.AddSwaggerGen();
        
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        return services;
    }
}