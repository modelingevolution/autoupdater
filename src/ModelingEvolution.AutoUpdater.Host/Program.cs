using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host.Components;
using ModelingEvolution.AutoUpdater.Host.Extensions;
using ModelingEvolution.RuntimeConfiguration;
using MudBlazor.Services;

namespace ModelingEvolution.AutoUpdater.Host;

public class Program
{
    public static void Configure(IConfigurationBuilder cm, string[] args)
    {
        cm.Sources.Clear();
        cm.SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", optional: true)
            .AddJsonFile($"appsettings.override.json", optional: true)
            .AddRuntimeConfiguration()
            .AddEnvironmentVariables()
            .AddCommandLine(args);
    }

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        
        // Apply custom configuration
        builder.Configuration.Sources.Clear();
        Configure(builder.Configuration, args);

        // Configure services
        builder.Services
            .AddMudServices()
            .AddAutoUpdater()
            .AddApplicationServices()
            .AddApiServices()
            .AddOpenApi()
            .AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        // Configure pipeline and endpoints
        app.ConfigurePipeline()
           .MapEndpoints()
           .MapOpenApi();

        app.Run();
    }
}