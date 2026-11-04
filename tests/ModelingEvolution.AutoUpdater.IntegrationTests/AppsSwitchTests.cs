using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Apps;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Host.Api.Apps;
using ModelingEvolution.AutoUpdater.Host.Extensions;
using ModelingEvolution.AutoUpdater.Host.Models;
using ModelingEvolution.AutoUpdater.Host.Services;
using ModelingEvolution.AutoUpdater.Services;
using MudBlazor.Services;
using NSubstitute;
using Xunit;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

/// <summary>
/// The AutoUpdater's own web app, as Program builds it, minus the SSH/Docker services no Apps page or layout needs: the
/// real pipeline (<c>ConfigurePipeline</c>, so <c>UseAppsGate</c> runs before the Razor endpoints), the real
/// <c>App</c>/<c>MainLayout</c>/<c>Apps</c> components, prerendered as a browser first receives them.
/// </summary>
public sealed class AutoUpdaterWebApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private AutoUpdaterWebApp(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }
    public IServiceProvider Services => _app.Services;

    /// <param name="services">Registrations added last, so they win: e.g. smaller limits.</param>
    public static async Task<AutoUpdaterWebApp> StartAsync(IDictionary<string, string?> configuration, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ModelingEvolution.AutoUpdater.Host.Components.App).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.WebHost.UseStaticWebAssets();

        builder.Services
            .AddMudServices()
            .AddApps(builder.Configuration)
            .AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services
            .AddSingleton<IBrandingService, BrandingService>()
            .AddSingleton<IEventHub, EventHub>()
            .AddSingleton<DockerComposeConfigurationModel>()
            .AddSingleton<PackageStateReadModel>()
            .AddSingleton(Substitute.For<IProgressService>());
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.ConfigurePipeline();
        app.MapAppsEndpoints();
        app.MapRazorComponents<ModelingEvolution.AutoUpdater.Host.Components.App>().AddInteractiveServerRenderMode();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new AutoUpdaterWebApp(app, new Uri(address.TrimEnd('/') + "/"));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>The Apps switch, on the real pipeline and the real components.</summary>
public sealed class AppsSwitchTests
{
    private const string MarkdownScript = "_content/MudBlazor.Markdown/MudBlazor.Markdown.min.js";

    private static Dictionary<string, string?> Enabled(Uri source) => new()
    {
        ["Apps:Enabled"] = "true",
        ["Apps:SourceUrl"] = source.ToString(),
        ["Apps:Title"] = "Downloads",
    };

    [Fact]
    public async Task Disabled_AppsIs404_NoNavEntry_NoMarkdownScript_NothingRegistered()
    {
        await using var web = await AutoUpdaterWebApp.StartAsync(new Dictionary<string, string?>());
        using var client = new HttpClient { BaseAddress = web.BaseAddress };

        var layout = await client.GetAsync("Error");
        var html = await layout.Content.ReadAsStringAsync();

        layout.StatusCode.Should().Be(HttpStatusCode.OK, "a page with the layout must render, or the absence checks below prove nothing");
        html.Should().Contain("Remote Support", "the navigation rendered");
        html.Should().NotContain("nav-apps").And.NotContain(MarkdownScript);
        (await client.GetAsync("apps")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("Apps/")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync(AppsOptions.DownloadPath("a.msi"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("apps/icons/viewer.png")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        web.Services.GetService<AppsCatalogClient>().Should().BeNull();
        web.Services.GetService<AppsFileProxy>().Should().BeNull();
    }

    [Fact]
    public async Task EnabledButMisconfigured_IsOffLikeDisabled()
    {
        await using var web = await AutoUpdaterWebApp.StartAsync(new Dictionary<string, string?> { ["Apps:Enabled"] = "true" });
        using var client = new HttpClient { BaseAddress = web.BaseAddress };

        web.Services.GetRequiredService<AppsSettings>().Error.Should().Contain("Apps:SourceUrl");
        (await client.GetAsync("apps")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        web.Services.GetService<AppsCatalogClient>().Should().BeNull();
    }

    [Fact]
    public async Task Enabled_ThePageNavEntryMarkdownScriptAndFileProxyAreServed()
    {
        await using var container = await new FakeAppsContainer
        {
            Files = [("viewer", "a.msi")],
            Serve = (context, _) => context.Response.WriteAsync("x"),
        }.StartAsync();
        await using var web = await AutoUpdaterWebApp.StartAsync(Enabled(container.BaseAddress));
        using var client = new HttpClient { BaseAddress = web.BaseAddress };

        var page = await client.GetAsync("apps");
        var html = await page.Content.ReadAsStringAsync();

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("nav-apps").And.Contain("Downloads").And.Contain(MarkdownScript);
        html.Should().Contain("apps-checking", "the page renders at once and reads the image only after it is shown");
        (await client.GetStringAsync(AppsOptions.DownloadPath("a.msi"))).Should().Be("x");
    }
}

/// <summary>GET /apps/icons/{name}: only a listed icon, and only a PNG within the cap, ever reaches a browser.</summary>
public sealed class AppsIconEndpointTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private static async Task<(FakeAppsContainer Container, AutoUpdaterWebApp Web, HttpClient Client)> StartAsync(byte[]? icon,
        Func<Task>? beforeIcon = null, AppIconLimits? limits = null)
    {
        var container = await new FakeAppsContainer
        {
            Files = [("viewer", "a.msi")],
            Serve = (context, _) => context.Response.WriteAsync("x"),
            IconName = "viewer.png",
            IconBytes = icon,
            BeforeIcon = beforeIcon,
        }.StartAsync();
        var web = await AutoUpdaterWebApp.StartAsync(new Dictionary<string, string?>
        {
            ["Apps:Enabled"] = "true",
            ["Apps:SourceUrl"] = container.BaseAddress.ToString(),
        }, services => { if (limits != null) services.AddSingleton(new AppIconSlots(limits)); });
        return (container, web, new HttpClient { BaseAddress = web.BaseAddress });
    }

    [Fact]
    public async Task ListedPng_IsServedAsPng_WithNosniff()
    {
        var (container, web, client) = await StartAsync(Png);
        await using (container)
        await using (web)
        using (client)
        {
            using var response = await client.GetAsync("apps/icons/viewer.png");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
        }
    }

    [Fact]
    public async Task WithItsVersion_TheBrowserMayKeepIt_WithoutOrStale_ItRevalidates()
    {
        var (container, web, client) = await StartAsync(Png);
        await using (container)
        await using (web)
        using (client)
        {
            var version = AppIcon.Version(Png);

            using var versioned = await client.GetAsync(AppIcon.Path("viewer.png", version));
            using var stale = await client.GetAsync(AppIcon.Path("viewer.png", "0000000000000000"));
            using var plain = await client.GetAsync("apps/icons/viewer.png");

            versioned.Headers.CacheControl!.ToString().Should().Contain("immutable").And.Contain("max-age=31536000");
            versioned.Headers.ETag!.Tag.Should().Be($"\"{version}\"");
            stale.Headers.CacheControl!.NoCache.Should().BeTrue();
            plain.Headers.CacheControl!.NoCache.Should().BeTrue();
        }
    }

    [Fact]
    public async Task IfNoneMatchWithItsETag_Is304_WithNoBody()
    {
        var (container, web, client) = await StartAsync(Png);
        await using (container)
        await using (web)
        using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "apps/icons/viewer.png");
            request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{AppIcon.Version(Png)}\"");

            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.NotModified);
            (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Head_IsAnsweredLikeGet_WithoutABody()
    {
        var (container, web, client) = await StartAsync(Png);
        await using (container)
        await using (web)
        using (client)
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "apps/icons/viewer.png"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        }
    }

    [Fact]
    public async Task MoreRequestsThanSlots_WaitThenGet503_AndTheImageIsAskedOnlyBySlotHolders()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (container, web, client) = await StartAsync(Png, () => release.Task, new AppIconLimits(1, TimeSpan.FromMilliseconds(300)));
        await using (container)
        await using (web)
        using (client)
        {
            var held = client.GetAsync("apps/icons/viewer.png");
            // The first request holds the only slot once the image has been asked.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (container.IconRequests == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
            container.IconRequests.Should().Be(1, "the first request reached the image");

            using var refused = await client.GetAsync("apps/icons/viewer.png");
            release.SetResult();
            using var served = await held;

            refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            refused.Headers.RetryAfter.Should().NotBeNull();
            served.StatusCode.Should().Be(HttpStatusCode.OK);
            container.IconRequests.Should().Be(1, "the refused request never reached the image");
        }
    }

    public static TheoryData<string, byte[]> HostileIcons() => new()
    {
        { "svg renamed .png", "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"u8.ToArray() },
        { "html renamed .png", "<html><script>alert(1)</script></html>"u8.ToArray() },
        { "wrong magic", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0B, 1, 2, 3] },
        { "empty", [] },
        { "over 256 KiB", [.. Png, .. new byte[AppIcon.MaxBytes]] },
    };

    [Theory]
    [MemberData(nameof(HostileIcons))]
    public async Task IconThatIsNotAPngWithinTheCap_Is404(string what, byte[] icon)
    {
        var (container, web, client) = await StartAsync(icon);
        await using (container)
        await using (web)
        using (client)
        {
            using var response = await client.GetAsync("apps/icons/viewer.png");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, what);
            container.IconRequests.Should().Be(1, "the image was asked, and its answer refused");
        }
    }

    [Theory]
    [InlineData("other.png")]
    [InlineData("viewer.svg")]
    [InlineData("..%2Fmanifest.json")]
    public async Task NameTheManifestDoesNotList_Is404_AndTheImageIsNeverAsked(string name)
    {
        var (container, web, client) = await StartAsync(Png);
        await using (container)
        await using (web)
        using (client)
        {
            using var response = await client.GetAsync("apps/icons/" + name);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            container.IconRequests.Should().Be(0);
        }
    }
}
