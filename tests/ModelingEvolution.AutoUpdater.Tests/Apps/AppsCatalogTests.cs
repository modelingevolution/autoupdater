using System.Net;
using System.Net.Sockets;
using System.Text;
using Bunit;
using FluentAssertions;
using Markdig;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelingEvolution.AutoUpdater.Apps;
using ModelingEvolution.AutoUpdater.Extensions;
using ModelingEvolution.AutoUpdater.Host.Components.Shared;
using MudBlazor;
using MudBlazor.Services;

namespace ModelingEvolution.AutoUpdater.Tests.Apps;

public class AppsManifestTests
{
    private static string Entry(string id = "viewer", string title = "Example Viewer", string? file = "\"version\":\"1.2.3\",\"file\":\"setup.exe\",\"size\":10", string? guide = "\"guide\":\"viewer.md\"")
    {
        var parts = new List<string> { $"\"id\":\"{id}\"", $"\"title\":{System.Text.Json.JsonSerializer.Serialize(title)}" };
        if (file != null) parts.Add(file);
        if (guide != null) parts.Add(guide);
        return "{" + string.Join(",", parts) + "}";
    }

    private static string Manifest(params string[] entries) =>
        $$"""{"schemaVersion":2,"release":"1.0.0","apps":[{{string.Join(",", entries)}}]}""";

    [Fact]
    public void Parse_ExampleFixture_ReadsBothAppsInOrder()
    {
        var catalog = AppsManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Apps", "manifest.json")));

        catalog.Reachable.Should().BeTrue();
        catalog.Release.Should().Be("1.2.0");
        catalog.Apps.Should().Equal(
            new AppEntry("windows", "Example Viewer for Windows", new AppFile("2.1.0", "ExampleViewer-2.1.0.msi", 48234496), "windows.md"),
            new AppEntry("android", "Example Viewer for Android", null, "android.md"));
    }

    [Fact]
    public void Parse_AppWithoutFileOrGuide_IsListedWithNeither()
    {
        var app = AppsManifest.Parse(Manifest(Entry(file: null, guide: null))).Find("viewer");

        app.Should().Be(new AppEntry("viewer", "Example Viewer", null, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>not json</html>")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"schemaVersion":2,"apps":"nope"}""")]
    public void Parse_BrokenManifest_IsUnreachableAndDoesNotThrow(string? manifest)
    {
        AppsManifest.Parse(manifest).Should().Be(AppsCatalog.Unreachable);
    }

    [Theory]
    [InlineData("""{"apps":[]}""", "?")]
    [InlineData("""{"schemaVersion":1,"apps":[]}""", "1")]
    [InlineData("""{"schemaVersion":3,"apps":[]}""", "3")]
    [InlineData("""{"schemaVersion":"2","apps":[]}""", "?")]
    [InlineData("""{"schemaVersion":2.5,"apps":[]}""", "2.5")]
    [InlineData("""{"schemaVersion":99999999999999999999,"apps":[]}""", "?")]
    public void Parse_UnknownSchemaVersion_IsRefusedWithWhatItDeclared(string manifest, string declared)
    {
        var catalog = AppsManifest.Parse(manifest);

        catalog.Reachable.Should().BeFalse();
        catalog.Apps.Should().BeEmpty();
        catalog.UnsupportedSchemaVersion.Should().Be(declared);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("-viewer")]
    [InlineData("view er")]
    [InlineData("viewer\n")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    [InlineData("")]
    public void Parse_InvalidId_DropsTheEntry(string id)
    {
        AppsManifest.Parse(Manifest(Entry(id: id))).Apps.Should().BeEmpty();
    }

    [Fact]
    public void Parse_TitleOverTheLimit_DropsTheEntry_AtTheLimitKeepsIt()
    {
        AppsManifest.Parse(Manifest(Entry(title: new string('x', 81)))).Apps.Should().BeEmpty();
        AppsManifest.Parse(Manifest(Entry(title: new string('x', 80)))).Apps.Should().ContainSingle();
    }

    public static TheoryData<string> HostileTitles() =>
    [
        "",
        "   ",
        "line\nbreak",
        "tab\there",
        "nul\0",
        "del" + (char)0x7F,
        "c1" + (char)0x85,
        "override" + (char)0x202E + "exe.txt",
        "isolate" + (char)0x2066,
        "separator" + (char)0x2028,
    ];

    [Theory]
    [MemberData(nameof(HostileTitles))]
    public void Parse_TitleThatIsNotPlainText_DropsTheEntry(string title)
    {
        AppsManifest.Parse(Manifest(Entry(title: title), Entry(id: "other"))).Apps.Select(a => a.Id).Should().Equal("other");
    }

    [Fact]
    public void Parse_EscapedLoneSurrogate_DropsTheEntryWithoutThrowing()
    {
        const string manifest = """{"schemaVersion":2,"apps":[{"id":"viewer","title":"bad \udc00 title"},{"id":"other","title":"Fine"}]}""";

        AppsManifest.Parse(manifest).Apps.Select(a => a.Id).Should().Equal("other");
    }

    [Fact]
    public void Parse_NonAsciiTitle_IsKept()
    {
        AppsManifest.Parse(Manifest(Entry(title: "Przeglądarka – wersja ✓"))).Find("viewer")!.Title.Should().Be("Przeglądarka – wersja ✓");
    }

    [Theory]
    [InlineData("\"version\":\"1\",\"file\":\"a.exe\"")]
    [InlineData("\"file\":\"a.exe\",\"size\":1")]
    [InlineData("\"version\":\"1\",\"size\":1")]
    [InlineData("\"version\":\"1\",\"file\":\"../../etc/passwd\",\"size\":1")]
    [InlineData("\"version\":\"1\",\"file\":\".hidden\",\"size\":1")]
    [InlineData("\"version\":\"1\",\"file\":\"a b.exe\",\"size\":1")]
    [InlineData("\"version\":\"1\",\"file\":\"a.exe\",\"size\":0")]
    [InlineData("\"version\":\"1\",\"file\":\"a.exe\",\"size\":-1")]
    [InlineData("\"version\":\"1\",\"file\":\"a.exe\",\"size\":\"10\"")]
    [InlineData("\"version\":\"1 2\",\"file\":\"a.exe\",\"size\":1")]
    [InlineData("\"version\":1,\"file\":\"a.exe\",\"size\":1")]
    public void Parse_IncompleteOrInvalidFileFields_DropsTheEntry(string fileFields)
    {
        AppsManifest.Parse(Manifest(Entry(file: fileFields))).Apps.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"guide\":\"viewer.txt\"")]
    [InlineData("\"guide\":\"../viewer.md\"")]
    [InlineData("\"guide\":\".md\"")]
    [InlineData("\"guide\":7")]
    [InlineData("\"guide\":\"a b.md\"")]
    public void Parse_InvalidGuideName_DropsTheEntry(string guide)
    {
        AppsManifest.Parse(Manifest(Entry(guide: guide))).Apps.Should().BeEmpty();
    }

    [Fact]
    public void Parse_DuplicateIdOrFileName_KeepsTheFirst()
    {
        var catalog = AppsManifest.Parse(Manifest(
            Entry(id: "a", title: "First"),
            Entry(id: "a", title: "Second", file: null),
            Entry(id: "b", title: "Same file")));

        catalog.Apps.Should().ContainSingle().Which.Title.Should().Be("First");
    }

    [Fact]
    public void Parse_MoreThanSixteenApps_ReadsOnlyTheFirstSixteen()
    {
        var entries = Enumerable.Range(0, 40).Select(i => Entry(id: $"app-{i}", file: null, guide: null)).ToArray();

        var catalog = AppsManifest.Parse(Manifest(entries));

        catalog.Apps.Should().HaveCount(AppsManifest.MaxApps);
        catalog.Apps[^1].Id.Should().Be("app-15");
    }

    [Fact]
    public void Parse_WrongJsonTypes_DropsThoseEntriesWithoutThrowing()
    {
        const string manifest = """{"schemaVersion":2,"release":5,"apps":[7,null,"x",[],{"id":1,"title":2},{"id":"ok","title":"Fine"}]}""";

        var catalog = AppsManifest.Parse(manifest);

        catalog.Reachable.Should().BeTrue();
        catalog.Release.Should().BeNull();
        catalog.Apps.Select(a => a.Id).Should().Equal("ok");
    }

    [Fact]
    public void Parse_DeeplyNestedDocument_IsUnreachableNotAStackOverflow()
    {
        var nested = new string('[', 10_000) + new string(']', 10_000);

        AppsManifest.Parse($$"""{"schemaVersion":2,"apps":[],"x":{{nested}}}""").Should().Be(AppsCatalog.Unreachable);
    }

    [Theory]
    [InlineData("ExampleViewer-2.1.0.msi", true)]
    [InlineData("a.exe\n", false)]
    [InlineData("..", false)]
    [InlineData("a%2Fb", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeFileName_OnlyAPlainName(string? name, bool safe)
    {
        AppsManifest.IsSafeFileName(name).Should().Be(safe);
    }
}

public class AppGuideMarkdownTests
{
    private static string Html(string markdown) => Markdown.ToHtml(markdown, AppGuideMarkdown.Pipeline);

    private static string Rendered(string markdown)
    {
        using var context = new BunitContext();
        context.Services.AddMudServices();
        context.Services.AddMudMarkdownServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context.Render<AppGuideView>(p => p.Add(v => v.Markdown, markdown)).Markup;
    }

    [Fact]
    public void Guide_OrdinaryMarkdown_RendersListsEmphasisAndHttpLinks()
    {
        var markup = Rendered("**Preview.** Android 12.\n\n1. Download it.\n2. See [the vendor](https://example.com/help).\n");

        markup.Should().Contain("Preview.").And.Contain("Download it.").And.Contain("href=\"https://example.com/help\"");
        markup.Should().Contain("<ol");
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("before <script>alert(1)</script> after")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<div onclick=\"alert(1)\">x</div>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    public void Guide_RawHtml_IsNeverInterpreted(string markdown)
    {
        foreach (var output in new[] { Html(markdown), Rendered(markdown) })
        {
            output.Should().NotContain("<script").And.NotContain("<img").And.NotContain("<iframe").And.NotContain("<div onclick");
        }
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JAVASCRIPT:alert(1))")]
    [InlineData("[click](  javascript:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:msgbox)")]
    [InlineData("[click](file:///etc/passwd)")]
    [InlineData("[click](//evil.example/x)")]
    [InlineData("[click][ref]\n\n[ref]: javascript:alert(1)")]
    [InlineData("<javascript:alert(1)>")]
    public void Guide_LinkToAnythingButHttpHttpsOrRelative_KeepsOnlyItsText(string markdown)
    {
        foreach (var output in new[] { Html(markdown), Rendered(markdown) })
        {
            output.Should().NotContainEquivalentOf("href=\"javascript").And.NotContainEquivalentOf("href=\"data:")
                .And.NotContainEquivalentOf("href=\"vbscript").And.NotContainEquivalentOf("href=\"file:")
                .And.NotContain("href=\"//");
        }
    }

    [Fact]
    public void Guide_LinkText_StaysWhenTheTargetGoes()
    {
        Html("Open [the settings](javascript:alert(1)) now.").Should().Contain("Open the settings now.");
    }

    [Theory]
    [InlineData("[ok](https://example.com)", "https://example.com")]
    [InlineData("[ok](http://example.com/a?b#c)", "http://example.com/a?b#c")]
    [InlineData("[ok](docs/install.html)", "docs/install.html")]
    [InlineData("[ok](#step-2)", "#step-2")]
    public void Guide_HttpHttpsAndRelativeLinks_AreKept(string markdown, string href)
    {
        Html(markdown).Should().Contain($"href=\"{href}\"");
    }

    [Theory]
    [InlineData("![tracking pixel](https://evil.example/p.png)")]
    [InlineData("![x](javascript:alert(1))")]
    [InlineData("![relative](shot.png)")]
    public void Guide_Images_AreReplacedByTheirAltText(string markdown)
    {
        foreach (var output in new[] { Html(markdown), Rendered(markdown) })
        {
            output.Should().NotContain("<img").And.NotContain("src=");
        }

        Html(markdown).Should().Contain(markdown[2..markdown.IndexOf(']')]);
    }

    [Fact]
    public void Decode_ValidUtf8_ReturnsTheText_WithoutABom()
    {
        AppGuideMarkdown.Decode([0xEF, 0xBB, 0xBF, .. "# Hi"u8]).Should().Be("# Hi");
    }

    [Fact]
    public void Decode_InvalidUtf8_IsUnavailable()
    {
        AppGuideMarkdown.Decode([0x61, 0xFF, 0x62]).Should().BeNull();
        AppGuideMarkdown.Decode([0x61, 0xC3]).Should().BeNull();
    }

    [Fact]
    public void Decode_OverTheCapOrEmpty_IsUnavailable()
    {
        AppGuideMarkdown.Decode(Encoding.UTF8.GetBytes(new string('a', AppGuideMarkdown.MaxBytes + 1))).Should().BeNull();
        AppGuideMarkdown.Decode(Encoding.UTF8.GetBytes(new string('a', AppGuideMarkdown.MaxBytes))).Should().NotBeNull();
        AppGuideMarkdown.Decode([]).Should().BeNull();
        AppGuideMarkdown.Decode("  \n "u8).Should().BeNull();
    }

    [Fact]
    public void Decode_NestingMarkdigRefuses_IsUnavailable_SoTheRendererNeverThrows()
    {
        var markdown = new string('>', 5000) + " deep";

        AppGuideMarkdown.Decode(Encoding.UTF8.GetBytes(markdown)).Should().BeNull();
    }

    [Fact]
    public void Decode_LargeOrdinaryGuide_IsAcceptedAndRenders()
    {
        var markdown = string.Concat(Enumerable.Repeat("1. A step with **bold** and `code`.\n", 1_700));

        var text = AppGuideMarkdown.Decode(Encoding.UTF8.GetBytes(markdown));

        text.Should().NotBeNull();
        ((Func<string>)(() => Html(text!))).Should().NotThrow();
    }

    [Fact]
    public void IsPlainText_LoneSurrogate_IsRefused()
    {
        AppsManifest.IsPlainText("lone" + (char)0xD800, 80).Should().BeFalse();
        AppsManifest.IsPlainText("pair" + char.ConvertFromUtf32(0x1F600), 80).Should().BeTrue();
    }
}

public class AppsCatalogClientTests
{
    private static readonly AppsOptions Options = new(new Uri("http://apps.test:8095/"));
    private const string Manifest = """{"schemaVersion":2,"release":"2.0.0","apps":[{"id":"viewer","title":"Example Viewer","version":"1","file":"a.apk","size":1,"guide":"viewer.md"}]}""";

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            return respond(request, cancellationToken);
        }
    }

    private static (AppsCatalogClient Client, StubHandler Handler) Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        return (new AppsCatalogClient(new HttpClient(handler), Options), handler);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task GetAsync_ImageAnswers_ReadsTheManifest_AndOnlyTheManifest()
    {
        var (client, handler) = Client((_, _) => Task.FromResult(Ok(Manifest)));

        var catalog = await client.GetAsync();

        catalog.Find("viewer")!.File!.FileName.Should().Be("a.apk");
        handler.Paths.Should().Equal("/manifest.json");
    }

    [Fact]
    public async Task GetAsync_EachCallReadsAgain_NothingIsCached()
    {
        var (client, handler) = Client((_, _) => Task.FromResult(Ok(Manifest)));

        await client.GetAsync();
        await client.GetAsync();

        handler.Paths.Should().Equal("/manifest.json", "/manifest.json");
    }

    [Fact]
    public async Task GetAsync_ConnectionRefused_IsUnreachable()
    {
        var (client, _) = Client((_, _) => throw new HttpRequestException("refused"));

        (await client.GetAsync()).Should().Be(AppsCatalog.Unreachable);
    }

    [Fact]
    public async Task GetAsync_NotFound_IsUnreachable()
    {
        var (client, _) = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        (await client.GetAsync()).Should().Be(AppsCatalog.Unreachable);
    }

    [Fact]
    public async Task GetAsync_ImageHangs_GivesUpWithinItsTimeout()
    {
        var (client, _) = Client(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            return Ok("{}");
        });

        var read = client.GetAsync();
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15)));

        finished.Should().BeSameAs(read);
        (await read).Should().Be(AppsCatalog.Unreachable);
    }

    [Fact]
    public async Task GetAsync_EndlessDocument_IsRefusedAndDoesNotExhaustMemory()
    {
        var (client, _) = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024))))
        }));

        (await client.GetAsync()).Should().Be(AppsCatalog.Unreachable);
    }

    [Fact]
    public async Task GetGuideAsync_ReadsTheGuideUnderGuides()
    {
        var (client, handler) = Client((_, _) => Task.FromResult(Ok("1. Install it.")));

        (await client.GetGuideAsync("viewer.md")).Should().Be("1. Install it.");
        handler.Paths.Should().Equal("/guides/viewer.md");
    }

    [Fact]
    public async Task GetGuideAsync_OverTheCap_IsUnavailable()
    {
        var (client, _) = Client((_, _) => Task.FromResult(Ok(new byte[AppGuideMarkdown.MaxBytes + 1].Select(_ => (byte)'a').ToArray())));

        (await client.GetGuideAsync("viewer.md")).Should().BeNull();
    }

    [Fact]
    public async Task GetGuideAsync_InvalidUtf8_IsUnavailable()
    {
        var (client, _) = Client((_, _) => Task.FromResult(Ok(new byte[] { 0x23, 0x20, 0xFF, 0xFE })));

        (await client.GetGuideAsync("viewer.md")).Should().BeNull();
    }

    [Theory]
    [InlineData("../manifest.json")]
    [InlineData("viewer.txt")]
    [InlineData("a/b.md")]
    public async Task GetGuideAsync_NameThatIsNotAGuide_IsNeverRequested(string name)
    {
        var (client, handler) = Client((_, _) => Task.FromResult(Ok("x")));

        (await client.GetGuideAsync(name)).Should().BeNull();
        handler.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task GetGuideAsync_Missing_IsUnavailable()
    {
        var (client, _) = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        (await client.GetGuideAsync("viewer.md")).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ImageRedirects_DoesNotFollowAndIsUnreachable()
    {
        // A real socket, so the production handler (not a stub) decides whether to follow.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestedPaths = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = socket.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var requestLine = await reader.ReadLineAsync(stop.Token) ?? "";
                    lock (requestedPaths) requestedPaths.Add(requestLine.Split(' ').ElementAtOrDefault(1) ?? "");
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                    var response = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        var client = new AppsCatalogClient(AppsCatalogClient.CreateHttpClient(), new AppsOptions(new Uri($"http://127.0.0.1:{port}/")));

        var catalog = await client.GetAsync();
        await stop.CancelAsync();
        listener.Stop();
        await server;

        catalog.Should().Be(AppsCatalog.Unreachable);
        lock (requestedPaths)
        {
            requestedPaths.Should().NotBeEmpty();
            requestedPaths.Should().NotContain("/elsewhere");
        }
    }
}

public class AppsSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
        .Build();

    [Fact]
    public void From_NothingConfigured_IsDisabledWithoutError()
    {
        var settings = AppsSettings.From(Config());

        settings.Enabled.Should().BeFalse();
        settings.Error.Should().BeNull();
    }

    [Fact]
    public void From_DisabledExplicitly_IgnoresTheRest()
    {
        AppsSettings.From(Config(("Apps:Enabled", "false"), ("Apps:SourceUrl", "nope"))).Should().Be(AppsSettings.Disabled);
    }

    [Fact]
    public void From_EnabledWithASourceUrl_UsesNeutralDefaults()
    {
        var settings = AppsSettings.From(Config(("Apps:Enabled", "true"), ("Apps:SourceUrl", "http://172.17.0.1:8095")));

        settings.Enabled.Should().BeTrue();
        settings.Options.Should().Be(new AppsOptions(new Uri("http://172.17.0.1:8095/"), null, "Apps"));
    }

    [Fact]
    public void From_EnabledWithEverything_ReadsTitleAndPackage()
    {
        var options = AppsSettings.From(Config(("Apps:Enabled", "True"), ("Apps:SourceUrl", "https://apps.local/base"),
            ("Apps:Title", "Downloads"), ("Apps:PackageName", "example-apps"))).Options!;

        options.SourceUrl.Should().Be(new Uri("https://apps.local/base/"));
        options.Title.Should().Be("Downloads");
        options.PackageName.Should().Be("example-apps");
        options.FileUrl("a.apk").Should().Be(new Uri("https://apps.local/base/files/a.apk"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("ftp://host/")]
    [InlineData("file:///srv/apps/")]
    [InlineData("/relative/")]
    [InlineData("http://user:pass@host/")]
    [InlineData("http://host/?q=1")]
    public void From_EnabledWithoutAUsableSourceUrl_StaysOffAndSaysWhy(string? url)
    {
        var values = new List<(string, string)> { ("Apps:Enabled", "true") };
        if (url != null) values.Add(("Apps:SourceUrl", url));

        var settings = AppsSettings.From(Config(values.ToArray()));

        settings.Enabled.Should().BeFalse();
        settings.Error.Should().Contain("Apps:SourceUrl");
    }

    [Fact]
    public void From_EnabledNotABoolean_StaysOffAndSaysWhy()
    {
        var settings = AppsSettings.From(Config(("Apps:Enabled", "yes")));

        settings.Enabled.Should().BeFalse();
        settings.Error.Should().Contain("Apps:Enabled");
    }

    [Fact]
    public void From_TitleThatIsNotPlainText_StaysOffAndSaysWhy()
    {
        var settings = AppsSettings.From(Config(("Apps:Enabled", "true"), ("Apps:SourceUrl", "http://h/"), ("Apps:Title", new string('x', 41))));

        settings.Enabled.Should().BeFalse();
        settings.Error.Should().Contain("Apps:Title");
    }

    [Theory]
    [InlineData("http://station.local:8080/apps", "http://station.local:8080/apps/files/ExampleViewer-2.1.0.msi")]
    [InlineData("https://station.local/", "https://station.local/apps/files/ExampleViewer-2.1.0.msi")]
    public void DownloadPath_IsOnThePagesOwnOrigin(string page, string expected)
    {
        // Relative, so the browser keeps the scheme, host and port it opened the page on (Blazor's base href is "/").
        var path = AppsOptions.DownloadPath("ExampleViewer-2.1.0.msi");

        path.Should().NotStartWith("/").And.NotContain("://");
        new Uri(new Uri(new Uri(page).GetLeftPart(UriPartial.Authority) + "/"), path).AbsoluteUri.Should().Be(expected);
    }
}

public class AppRowTests
{
    private static readonly AppFile File = new("1", "a.exe", 1);
    private static readonly AppEntry WithFile = new("viewer", "Example Viewer", File, "viewer.md");
    private static readonly AppEntry WithoutFile = new("other", "Other", null, null);
    private static readonly AppsCatalog Listed = new(true, "1.0.0", [WithFile, WithoutFile]);
    private static readonly AppsPackageSnapshot Deployed = new(true, true, false);

    [Fact]
    public void Decide_File_IsReady_NoFile_IsNotAvailableYet()
    {
        AppRow.Decide(File, Deployed).Should().Be(AppStatus.Ready);
        AppRow.Decide(null, Deployed).Should().Be(AppStatus.NotAvailableYet);
    }

    [Fact]
    public void Decide_UpdateRunning_WinsOverEverything()
    {
        AppRow.Decide(File, new AppsPackageSnapshot(true, true, true)).Should().Be(AppStatus.Downloading);
    }

    [Fact]
    public void For_ReadableCatalog_OneRowPerAppInOrder_OnlyAFileCanBeDownloaded()
    {
        var rows = AppRow.For(Listed, Deployed);

        rows.Select(r => r.Id).Should().Equal("viewer", "other");
        rows[0].CanDownload.Should().BeTrue();
        rows[1].CanDownload.Should().BeFalse();
        rows[0].Guide.Should().Be(GuideState.Loading);
        rows[1].Guide.Should().Be(GuideState.None);
    }

    [Fact]
    public void For_UnreadableCatalog_InventsNoRows()
    {
        AppRow.For(null, Deployed).Should().BeEmpty();
        AppRow.For(AppsCatalog.Unreachable, Deployed).Should().BeEmpty();
        AppRow.For(AppsCatalog.Unsupported("1"), Deployed).Should().BeEmpty();
    }

    [Fact]
    public void SetGuide_NullIsUnavailable_TextIsLoaded()
    {
        var unavailable = new AppRow(WithFile);
        var loaded = new AppRow(WithFile);

        unavailable.SetGuide(null);
        loaded.SetGuide("1. Install.");

        unavailable.Guide.Should().Be(GuideState.Unavailable);
        loaded.Guide.Should().Be(GuideState.Loaded);
        loaded.GuideMarkdown.Should().Be("1. Install.");
    }

    [Theory]
    [InlineData(true, false, AppsPageStatus.NotDownloaded)]
    [InlineData(true, true, AppsPageStatus.Unavailable)]
    [InlineData(false, false, AppsPageStatus.Unavailable)]
    public void DecidePage_ImageNotAnswering_DependsOnThePackage(bool registered, bool deployed, AppsPageStatus expected)
    {
        AppRow.DecidePage(AppsCatalog.Unreachable, new AppsPackageSnapshot(registered, deployed, false)).Should().Be(expected);
    }

    [Fact]
    public void DecidePage_CoversCheckingUnsupportedEmptyAndListed()
    {
        AppRow.DecidePage(null, Deployed).Should().Be(AppsPageStatus.Checking);
        AppRow.DecidePage(AppsCatalog.Unsupported("1"), Deployed).Should().Be(AppsPageStatus.UnsupportedSchema);
        AppRow.DecidePage(new AppsCatalog(true, null, []), Deployed).Should().Be(AppsPageStatus.Empty);
        AppRow.DecidePage(Listed, Deployed).Should().Be(AppsPageStatus.Listed);
    }
}

public class AppsFailureTextTests
{
    private const string SeeLog = " Details are in the AutoUpdater log.";
    private const string Neutral = "The update failed. Details are in the AutoUpdater log.";
    private const string Credentials = "The registry refused this station's credentials." + SeeLog;
    private const string Network = "The station could not reach the registry." + SeeLog;
    private const string NotFound = "The release was not found in the registry." + SeeLog;
    private const string Disk = "The station ran out of disk space." + SeeLog;
    private const string Busy = "Another update is already running." + SeeLog;

    private static readonly string[] AllSentences = [Neutral, Credentials, Network, NotFound, Disk, Busy];

    [Fact]
    public void Neutral_IsTheAgreedSentence()
    {
        AppsFailureText.Neutral.Should().Be(Neutral);
    }

    // Only a signal with one meaning names a cause.
    [Theory]
    [InlineData("Error response from daemon: Head \"https://docker.example.test/v2/example/apps/manifests/1.2.0\": unauthorized: authentication required", Credentials)]
    [InlineData("failed to authorize: failed to fetch oauth token: unexpected status: 401 Unauthorized", Credentials)]
    [InlineData("unauthorized: authentication required (repository not found)", Credentials)]
    [InlineData("Error response from daemon: manifest for docker.example.test/example/apps:1.2.0 not found: manifest unknown: manifest unknown", NotFound)]
    [InlineData("docker.example.test/example/apps:1.2.0: manifest unknown", NotFound)]
    [InlineData("name unknown: repository name not known to registry", NotFound)]
    [InlineData("Get \"https://docker.example.test/v2/\": dial tcp: lookup docker.example.test on 127.0.0.11:53: no such host", Network)]
    [InlineData("Get \"https://docker.example.test/v2/\": dial tcp 192.0.2.10:443: connect: connection refused", Network)]
    [InlineData("dial tcp [fd00::1:2]:443: connect: connection refused", Network)]
    [InlineData("write /var/lib/docker/tmp/GetImageBlob123: no space left on device", Disk)]
    [InlineData("Update already in progress", Busy)]
    public void Describe_UnambiguousSignal_NamesItsCause(string failure, string expected)
    {
        AppsFailureText.Describe(failure).Should().Be(expected);
    }

    // Each of these would have matched a looser word ("denied", "not found", "network", "timed out") and named the wrong cause.
    [Theory]
    [InlineData("Got permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock: connect: permission denied")]
    [InlineData("open /data/example-apps/.env: permission denied")]
    [InlineData("deploy@station: Permission denied (publickey).")]
    [InlineData("git@github.com:example/example-apps-compose.git: Permission denied (publickey)")]
    [InlineData("bash: docker: command not found")]
    [InlineData("sudo: up-1.2.0.sh: command not found")]
    [InlineData("network example_default not found")]
    [InlineData("Error response from daemon: network 3f2a9c not found")]
    [InlineData("no such service: example-apps")]
    [InlineData("SSH command timed out after 300 seconds: docker compose up -d")]
    [InlineData("ssh: connect to host station-7 port 22: Connection timed out")]
    [InlineData("ssh: connect to host station-7 port 22: Connection refused")]
    [InlineData("dial tcp 192.168.30.29:443: i/o timeout")]
    [InlineData("Error response from daemon: pull access denied for x, repository does not exist or may require 'docker login'")]
    [InlineData("failed to pull docker.example.test/example/apps:1.2.0: denied")]
    [InlineData("docker login -u robot --password hunter2 failed")]
    public void Describe_AmbiguousOrLocalFailure_IsNeutral(string failure)
    {
        AppsFailureText.Describe(failure).Should().Be(Neutral);
    }

    // The page never repeats any part of the failure: whatever comes in, one fixed sentence goes out.
    [Theory]
    [InlineData("HARBOR_PASSWORD=hunter2 rejected", "hunter2")]
    [InlineData("access_token=abc123def failed", "abc123def")]
    [InlineData("api_key=abc123 failed", "abc123")]
    [InlineData("""{"password":"hunter2"}""", "hunter2")]
    [InlineData("docker login -u robot --password hunter2 failed", "hunter2")]
    [InlineData("dial tcp: lookup buildhost on 127.0.0.11:53: no such host", "buildhost")]
    [InlineData("dial tcp [fd00::1:2]:443: connect: connection refused", "fd00")]
    [InlineData("ssh: connect to host fe80::1%eth0 port 22: Connection timed out", "fe80")]
    [InlineData("git@github.com:example/example-apps-compose.git: Permission denied", "example-apps-compose")]
    [InlineData("ssh: connect to host station-7 port 22: Connection refused", "station-7")]
    [InlineData("X-Registry-Auth: eyJhbGciOiJIUzI1NiJ9 rejected", "eyJ")]
    [InlineData("token:abc auth=xyz", "xyz")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz unauthorized: denied", "dXNlcjpwYXNz")]
    [InlineData("clone of https://user:s3cret@github.com/x/y.git failed", "s3cret")]
    [InlineData("Get \"http://172.17.0.1:8095/manifest.json\": EOF", "172.17.0.1")]
    [InlineData("failed reading nginx.conf and appsettings.Production.json", "appsettings")]
    public void Describe_NeverRepeatsAnyPartOfTheFailure(string failure, string secret)
    {
        var text = AppsFailureText.Describe(failure);

        text.Should().NotContainEquivalentOf(secret);
        AllSentences.Should().Contain(text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Describe_NoMessage_IsNeutral(string? failure)
    {
        AppsFailureText.Describe(failure).Should().Be(Neutral);
    }
}

public class AppsConfigurationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
        .Build();

    [Fact]
    public void DockerHostAddress_NothingConfigured_IsTheBridgeGateway()
    {
        Config().DockerHostAddress().Should().Be(ModelingEvolution.AutoUpdater.Extensions.ConfigurationExtensions.DefaultDockerHost).And.Be("172.17.0.1");
    }

    [Fact]
    public void DockerHostAddress_PrefersSshHostOverHostAddress()
    {
        Config(("SshHost", "10.0.0.9"), ("HostAddress", "10.0.0.8")).DockerHostAddress().Should().Be("10.0.0.9");
        Config(("HostAddress", "10.0.0.8")).DockerHostAddress().Should().Be("10.0.0.8");
    }
}
