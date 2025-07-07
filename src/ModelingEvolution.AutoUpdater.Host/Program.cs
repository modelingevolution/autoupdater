using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host.Components;
using ModelingEvolution.AutoUpdater.Host.Extensions;
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
            .AddJsonFile($"/data/appsettings.json", optional: true, true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();
    }

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        Configure(builder.Configuration, args);

        // Configure services
        builder.Services
            .AddMudServices()
            .AddAutoUpdater()
            .AddApplicationServices()
            .AddApiServices()
            .AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        // Configure pipeline and endpoints
        app.ConfigurePipeline()
           .MapEndpoints();

        app.Run();
    }
}