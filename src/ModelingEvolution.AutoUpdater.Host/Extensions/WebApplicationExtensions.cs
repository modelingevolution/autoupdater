using ModelingEvolution.AutoUpdater.Host.Api.AutoUpdater;
using ModelingEvolution.AutoUpdater.Host.Api.Backup;

namespace ModelingEvolution.AutoUpdater.Host.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication ConfigurePipeline(this WebApplication app)
    {
        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
        }

        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapStaticAssets();
        return app;
    }

    public static WebApplication MapEndpoints(this WebApplication app)
    {
        app.MapAutoUpdaterEndpoints();
        app.MapBackupEndpoints();

        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode();

        return app;
    }
}