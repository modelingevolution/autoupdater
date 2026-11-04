using ModelingEvolution.AutoUpdater.Apps;
using ModelingEvolution.AutoUpdater.Extensions;
using MudBlazor;

namespace ModelingEvolution.AutoUpdater.Host.Api.Apps;

/// <summary>
/// The icon endpoint's cap: requests that read the image at once; one more waits up to <paramref name="Wait"/> for a slot,
/// then gets 503. A page's icons are cached by the browser, so the cap is met only by a client that keeps asking.
/// </summary>
public sealed record AppIconLimits(int MaxConcurrent, TimeSpan Wait)
{
    public static readonly AppIconLimits Default = new(4, TimeSpan.FromSeconds(5));
}

/// <summary>The icon endpoint's slots, one set for the AutoUpdater.</summary>
public sealed class AppIconSlots(AppIconLimits limits)
{
    public AppIconLimits Limits { get; } = limits;
    public SemaphoreSlim Slots { get; } = new(limits.MaxConcurrent, limits.MaxConcurrent);
}

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
    public static IServiceCollection AddApps(this IServiceCollection services, AppsSettings settings, AppsFileProxyLimits? limits = null,
        AppIconLimits? iconLimits = null)
    {
        services.AddSingleton(settings);
        if (settings.Options is not { } options) return services;

        services.AddSingleton(options);
        services.AddMudMarkdownServices();
        services.AddSingleton(_ => new AppsCatalogClient(options));
        services.AddSingleton(sp => new AppsFileProxy(sp.GetRequiredService<AppsCatalogClient>(), sp.GetRequiredService<ILogger<AppsFileProxy>>(), limits));
        services.AddSingleton(new AppIconSlots(iconLimits ?? AppIconLimits.Default));
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
    /// <see cref="AppsFileProxy"/>, and GET and HEAD <c>/apps/icons/{name}</c>, an app's icon.
    /// </summary>
    public static IEndpointRouteBuilder MapAppsEndpoints(this IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetService<AppsSettings>() is not { Enabled: true }) return app;

        app.MapMethods("/apps/files/{name}", [HttpMethods.Get, HttpMethods.Head],
                (HttpContext context, string name, AppsFileProxy proxy) => proxy.HandleAsync(context, name))
            .ExcludeFromDescription();
        app.MapMethods("/apps/icons/{name}", [HttpMethods.Get, HttpMethods.Head], ServeIconAsync).ExcludeFromDescription();
        return app;
    }

    /// <summary>
    /// An icon the manifest lists, read from the image and served only when it is a PNG within the size cap; anything else
    /// is a 404 (the page then shows its neutral icon). It is small, so it is read whole rather than streamed. Its ETag is
    /// <see cref="AppIcon.Version"/>; asked for with that version (<c>?v=</c>, as the page links it), the browser may keep
    /// it for good, since another icon gets another URL.
    /// </summary>
    private static async Task<IResult> ServeIconAsync(HttpContext context, string name, AppsCatalogClient client, AppIconSlots slots)
    {
        if (!AppsManifest.IsIconName(name)) return Results.NotFound();
        var aborted = context.RequestAborted;
        if (!await slots.Slots.WaitAsync(slots.Limits.Wait, aborted))
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var catalog = await client.GetAsync(aborted);
            if (!catalog.Icons.Contains(name, StringComparer.Ordinal)) return Results.NotFound();

            var bytes = await client.GetIconAsync(name, aborted);
            if (bytes == null) return Results.NotFound();

            var version = AppIcon.Version(bytes);
            var etag = $"\"{version}\"";
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = "default-src 'none'";
            headers.ETag = etag;
            headers.CacheControl = context.Request.Query["v"] == version ? "private, max-age=31536000, immutable" : "no-cache";
            if (context.Request.Headers.IfNoneMatch.Any(v => v != null && v.Split(',').Any(t => t.Trim() == etag || t.Trim() == "*")))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(bytes, AppIcon.ContentType);
        }
        finally
        {
            slots.Slots.Release();
        }
    }
}
