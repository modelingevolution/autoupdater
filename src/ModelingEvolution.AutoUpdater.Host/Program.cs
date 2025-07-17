using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host;
using ModelingEvolution.AutoUpdater.Host.Components;
using ModelingEvolution.AutoUpdater.Host.Extensions;
using ModelingEvolution.AutoUpdater.Extensions;
using ModelingEvolution.RuntimeConfiguration;
using MudBlazor.Services;
using System.Reflection;
using static System.Console;

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

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        
        // Apply custom configuration
        builder.Configuration.Sources.Clear();
        Configure(builder.Configuration, args);
        
        WriteConsoleHeader(builder);

        // Configure services
        builder.Services
            .AddMudServices()
            .AddAutoUpdaterHost()
            .AddApiServices()
            .AddOpenApi()
            .AddRazorComponents()
            .AddInteractiveServerComponents();

        // Add AutoUpdater with async initialization
        await builder.Services.AddAutoUpdaterAsync(builder.Configuration);

        var app = builder.Build();

        // Display configuration values at startup
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        app.Configuration.DisplayConfigurationValues(logger);

        // Configure pipeline and endpoints
        app.ConfigurePipeline()
            .MapEndpoints()
            .MapOpenApi();

        await app.RunAsync();
    }

    private static string GetApplicationVersion()
    {
        try
        {
            // Priority 1: Check for app.version file
            if (File.Exists("app.version"))
            {
                var fileVersion = File.ReadAllText("app.version").Trim();
                return $"v{fileVersion}";
            }
            else
            {
                // Priority 2: Fallback to GitCommitShaAttribute
                var assembly = Assembly.GetExecutingAssembly();
                var gitCommitSha = assembly.GetCustomAttribute<GitCommitShaAttribute>();
                
                if (gitCommitSha != null)
                {
                    return $"dev-{gitCommitSha.ShortSha}";
                }
                else
                {
                    // Fallback to assembly version
                    return assembly.GetName().Version?.ToString() ?? "Unknown";
                }
            }
        }
        catch
        {
            return "Unknown";
        }
    }

    private static void WriteConsoleHeader(WebApplicationBuilder builder)
    {
        var color = ForegroundColor;
        string version = GetApplicationVersion();
        ForegroundColor = ConsoleColor.Cyan;
        WriteLine("*=================================================================*");
        WriteLine("*                                                                 *");
        WriteLine("*     _         _         _   _           _       _               *");
        WriteLine("*    / \\  _   _| |_ ___  | | | |_ __   __| | __ _| |_ ___ _ __    *");
        WriteLine("*   / _ \\| | | | __/ _ \\ | | | | '_ \\ / _` |/ _` | __/ _ \\ '__|   *");
        WriteLine("*  / ___ \\ |_| | || (_) || |_| | |_) | (_| | (_| | ||  __/ |      *");
        WriteLine("* /_/   \\_\\__,_|\\__\\___/  \\___/| .__/ \\__,_|\\__,_|\\__\\___|_|      *");
        WriteLine("*                              |_|                                *");
        WriteLine("*                                                                 *");
        WriteLine("*=================================================================*");
        WriteLine(("version: " + version).PadRight(65));
        ForegroundColor = color;
        WriteLine();
        WriteHeader("Server options:");
        WriteConfigurationValue("Environment", builder.Environment.EnvironmentName, "ASPNETCORE_ENVIRONMENT");
        WriteConfigurationValue("Content root path", builder.Environment.ContentRootPath);
        WriteConfigurationValue("Web root path", builder.Environment.WebRootPath);
        WriteConfigurationValue("Application name", builder.Environment.ApplicationName);
        WriteConfigurationValue("Host name", builder.Configuration.GetValue<string>("HostName") ?? Environment.MachineName, "HostName");
        WriteConfigurationValue("Allowed hosts", builder.Configuration.GetValue<string>("AllowedHosts") ?? "*", "AllowedHosts");
        WriteLine();

        WriteHeader("AutoUpdater options:");
        WriteConfigurationValue("SSH Host", builder.Configuration.SshHost() ?? "Not configured", "SshHost");
        WriteConfigurationValue("SSH User", builder.Configuration.SshUser() ?? "Not configured", "SshUser");
        WriteConfigurationValue("SSH Port", builder.Configuration.SshPort().ToString(), "SshPort");
        WriteConfigurationValue("VPN Provider", builder.Configuration.VpnProvider(), "VpnProvider");
        WriteConfigurationValue("VPN Provider Access", builder.Configuration.VpnProviderAccess(), "VpnProviderAccess");
        WriteLine();
    }

    private static void WriteHeader(string text)
    {
        var color = ForegroundColor;
        ForegroundColor = ConsoleColor.Yellow;
        WriteLine(text);
        ForegroundColor = color;
    }

    private static void WriteConfigurationValue(string key, string value, string? configKey = null)
    {
        var color = ForegroundColor;
        ForegroundColor = ConsoleColor.Gray;
        Write($"  {key}: ");
        ForegroundColor = ConsoleColor.White;
        Write(value);
        if (configKey != null)
        {
            ForegroundColor = ConsoleColor.DarkGray;
            Write($" ({configKey})");
        }
        WriteLine();
        ForegroundColor = color;
    }
}