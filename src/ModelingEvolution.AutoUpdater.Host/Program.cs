using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host.Components;
using MudBlazor.Services;

namespace ModelingEvolution.AutoUpdater.Host
{
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

            // Add services to the container.
            builder.Services
                .AddMudServices()
                .AddAutoUpdater()
                .AddRazorComponents()
                .AddInteractiveServerComponents();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
