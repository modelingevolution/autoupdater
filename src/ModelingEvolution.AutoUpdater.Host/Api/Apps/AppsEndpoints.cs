using ModelingEvolution.AutoUpdater.Apps;
using ModelingEvolution.AutoUpdater.Extensions;
using MudBlazor;

namespace ModelingEvolution.AutoUpdater.Host.Api.Apps;

/// <summary>
/// The Apps feature's wiring. All of it hangs on <see cref="AppsSettings"/>: disabled (the default), nothing is registered
/// but the settings, no endpoint is mapped, and <c>/apps</c> and everything under it answer 404.
/// </summary>
public static class AppsEndpoints
{
    /// <summary>Reads the <c>Apps:*</c> settings once and registers them (see the other overload).</summary>
    public static IServiceCollection AddApps(this IServiceCollection services, IConfiguration configuration) =>
        services.AddApps(configuration.AppsSettings());

    /// <summary>Registers <see cref="AppsSettings"/> always, and the markdown renderer, catalog client and file proxy only when the page is enabled.</summary>
    public static IServiceCollection AddApps(this IServiceCollection services, AppsSettings settings, AppsFileProxyLimits? limits = null)
    {
        services.AddSingleton(settings);
        if (settings.Options is not { } options) return services;

        services.AddSingleton(options);
        services.AddMudMarkdownServices();
        services.AddSingleton(_ => new AppsCatalogClient(options));
        services.AddSingleton(sp => new AppsFileProxy(sp.GetRequiredService<AppsCatalogClient>(), sp.GetRequiredService<ILogger<AppsFileProxy>>(), limits));
        return services;
    }

    /// <summary>
    /// With the page disabled, answers 404 for <c>/apps</c> and <c>/apps/...</c> before any endpoint runs, so the Razor page
    /// that is compiled in is not served either.
    /// </summary>
    public static IApplicationBuilder UseAppsGate(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService<AppsSettings>() is { Enabled: true }) return app;
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/apps"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });
    }

    /// <summary>
    /// Only when enabled: GET and HEAD <c>/apps/files/{name}</c>, the apps container's file through
    /// <see cref="AppsFileProxy"/>, and GET <c>/apps/icons/{name}</c>, an app's icon.
    /// </summary>
    public static IEndpointRouteBuilder MapAppsEndpoints(this IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetService<AppsSettings>() is not { Enabled: true }) return app;

        app.MapMethods("/apps/files/{name}", [HttpMethods.Get, HttpMethods.Head],
                (HttpContext context, string name, AppsFileProxy proxy) => proxy.HandleAsync(context, name))
            .ExcludeFromDescription();
        app.MapGet("/apps/icons/{name}", ServeIconAsync).ExcludeFromDescription();
        return app;
    }

    /// <summary>
    /// An icon the manifest lists, read from the image and served only when it is a PNG within the size cap; anything else
    /// is a 404 (the page then shows its neutral icon). It is small, so it is read whole rather than streamed.
    /// </summary>
    private static async Task<IResult> ServeIconAsync(HttpContext context, string name, AppsCatalogClient client)
    {
        if (!AppsManifest.IsIconName(name)) return Results.NotFound();
        var aborted = context.RequestAborted;
        var catalog = await client.GetAsync(aborted);
        if (!catalog.Icons.Contains(name, StringComparer.Ordinal)) return Results.NotFound();

        var bytes = await client.GetIconAsync(name, aborted);
        if (bytes == null) return Results.NotFound();

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        return Results.Bytes(bytes, AppIcon.ContentType);
    }
}
