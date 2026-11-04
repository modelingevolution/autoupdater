using System.Net;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.AutoUpdater.Apps;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Host.Components.Pages;
using ModelingEvolution.AutoUpdater.Host.Models;
using ModelingEvolution.AutoUpdater.Services;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

namespace ModelingEvolution.AutoUpdater.Tests.Apps;

/// <summary>The Apps page as it renders in a browser circuit: real AppsView, AppGuideView and MudMarkdown; a stub image.</summary>
public sealed class AppsViewTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly AppsOptions Options = new(new Uri("http://apps.test:8095/"), null, "Downloads");

    private readonly BunitContext _context = new();
    private readonly Dictionary<string, Func<HttpResponseMessage>> _image = new();

    public AppsViewTests()
    {
        _context.Services.AddMudServices();
        _context.Services.AddMudMarkdownServices();
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _context.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _context.Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        _context.Services.AddSingleton<IEventHub, EventHub>();
        _context.Services.AddSingleton<DockerComposeConfigurationModel>();
        _context.Services.AddSingleton<PackageStateReadModel>();
        _context.Services.AddSingleton(Substitute.For<IProgressService>());
        var handler = new AppsCatalogClientTests.StubHandler((request, _) =>
            Task.FromResult(_image.TryGetValue(request.RequestUri!.AbsolutePath, out var respond) ? respond() : new HttpResponseMessage(HttpStatusCode.NotFound)));
        _context.Services.AddSingleton(new AppsCatalogClient(new HttpClient(handler), Options));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private void Serve(string path, string body) => _image[path] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    private void Serve(string path, byte[] body) => _image[path] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private IRenderedComponent<AppsView> Open() => _context.Render<AppsView>(p => p.Add(v => v.Options, Options));

    [Fact]
    public void OneAppOneDownload_ShowsNameIconDownloadAndGuide()
    {
        Serve("/manifest.json", ManifestJson.Manifest(ManifestJson.App(id: "viewer", name: "Example Viewer", icon: "\"icon\":\"viewer.png\"",
            ManifestJson.Download(extra: "\"version\":\"2.1.0\",\"file\":\"Viewer-2.1.0.msi\",\"size\":2048,\"guide\":\"windows.md\""))));
        Serve("/icons/viewer.png", Png);
        Serve("/guides/windows.md", "1. Run **the installer**.\n2. See [help](https://example.com/help).");

        var page = Open();

        page.WaitForAssertion(() => page.Find("[data-testid=download-guide] ol").Should().NotBeNull());
        page.WaitForAssertion(() => page.Find("[data-testid=app-icon]").GetAttribute("src").Should().Be("apps/icons/viewer.png"));
        var card = page.Find("[data-testid=app-viewer]");
        card.QuerySelector("[data-testid=app-name]")!.TextContent.Should().Be("Example Viewer");
        card.QuerySelector("[data-testid=download-title]")!.TextContent.Should().Be("Windows · x64");
        card.QuerySelector("[data-testid=download-status]")!.TextContent.Should().Contain("Ready");
        card.QuerySelector("[data-testid=download-version]")!.TextContent.Should().Be("Version 2.1.0");
        card.QuerySelector("[data-testid=download-button]")!.GetAttribute("href").Should().Be("apps/files/Viewer-2.1.0.msi");
        card.QuerySelector("[data-testid=download-guide] b")!.TextContent.Should().Be("the installer");
        card.QuerySelector("[data-testid=download-guide] a")!.GetAttribute("href").Should().Be("https://example.com/help");
        card.QuerySelectorAll("[data-testid=download-preview]").Should().BeEmpty();
    }

    [Fact]
    public void Downloads_ShowLabelPreviewAndNotAvailableYet_InManifestOrder()
    {
        Serve("/manifest.json", ManifestJson.Manifest(ManifestJson.App(id: "viewer", name: "Example Viewer", icon: null,
            ManifestJson.Download("linux", "arm64", "\"label\":\"Kiosk devices\",\"version\":\"1\",\"file\":\"v.deb\",\"size\":1"),
            ManifestJson.Download("android", "arm64", "\"preview\":true"),
            ManifestJson.Download("macos", "any", null))));

        var page = Open();

        page.WaitForAssertion(() => page.FindAll("[data-testid=download-title]").Should().HaveCount(3));
        page.FindAll("[data-testid=download-title]").Select(e => e.TextContent)
            .Should().Equal("Linux · arm64 · Kiosk devices", "Android · arm64", "macOS");
        page.FindAll("[data-testid=download-preview]").Should().ContainSingle();
        page.Find("[data-testid=download-android-arm64-] [data-testid=download-status]").TextContent.Should().Contain("Not available yet");
        page.FindAll("[data-testid=download-button]").Should().ContainSingle();
        page.Find("[data-testid=app-icon-default]").Should().NotBeNull("no icon listed");
    }

    [Fact]
    public void OneGuideFails_OnlyThatDownloadSaysUnavailable_NeverASpinner()
    {
        Serve("/manifest.json", ManifestJson.Manifest(ManifestJson.App(id: "viewer", name: "Example Viewer", icon: null,
            ManifestJson.Download("windows", "x64", "\"guide\":\"windows.md\""),
            ManifestJson.Download("linux", "arm64", "\"guide\":\"linux.md\""),
            ManifestJson.Download("android", "arm64", "\"guide\":\"android.md\""))));
        Serve("/guides/windows.md", "1. Install it.");
        // An exception the client does not expect: it escapes the read, and still only this download is affected.
        _image["/guides/linux.md"] = () => throw new InvalidDataException("broken");
        Serve("/guides/android.md", "[a](" + new string('[', 20_000));

        var page = Open();

        page.WaitForAssertion(() => page.FindAll("[data-testid=download-guide-loading]").Should().BeEmpty());
        page.Find("[data-testid=download-windows-x64-] [data-testid=download-guide]").TextContent.Should().Contain("Install it.");
        page.Find("[data-testid=download-linux-arm64-] [data-testid=download-guide-unavailable]").TextContent.Should().Be("Install guide unavailable.");
        page.Find("[data-testid=download-android-arm64-] [data-testid=download-guide-unavailable]").TextContent.Should().Be("Install guide unavailable.");
    }

    [Theory]
    [InlineData("svg")]
    [InlineData("missing")]
    public void IconThatIsNotAPng_ShowsTheNeutralIcon(string answer)
    {
        Serve("/manifest.json", ManifestJson.Manifest(ManifestJson.App(id: "viewer", name: "Example Viewer", icon: "\"icon\":\"viewer.png\"",
            ManifestJson.Download(extra: null))));
        if (answer == "svg") Serve("/icons/viewer.png", "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>");

        var page = Open();

        page.WaitForAssertion(() => page.Find("[data-icon-state]").GetAttribute("data-icon-state").Should().Be(nameof(LoadState.Unavailable)));
        page.Find("[data-testid=app-icon-default]").Should().NotBeNull();
        page.FindAll("[data-testid=app-icon]").Should().BeEmpty();
        page.FindAll("img").Should().BeEmpty();
    }

    [Fact]
    public void ImageNotAnswering_SaysSo_AndShowsNoCard()
    {
        var page = Open();

        page.WaitForAssertion(() => page.Find("[data-testid=apps-unavailable]").Should().NotBeNull());
        page.FindAll("[data-testid^=app-]").Where(e => e.GetAttribute("data-testid") != "apps-unavailable").Should().BeEmpty();
    }
}
