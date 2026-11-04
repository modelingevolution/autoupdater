using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ModelingEvolution.AutoUpdater.Apps;
using ModelingEvolution.AutoUpdater.Extensions;

namespace ModelingEvolution.AutoUpdater.Tests.Apps;

/// <summary>Builds manifest JSON for the tests: an app with downloads, each written as raw JSON members.</summary>
internal static class ManifestJson
{
    public const string WindowsFile = "\"version\":\"1.2.3\",\"file\":\"setup.exe\",\"size\":10";

    public static string Download(string platform = "windows", string architecture = "x64", string? extra = WindowsFile) =>
        "{" + $"\"platform\":\"{platform}\",\"architecture\":\"{architecture}\"" + (extra == null ? "" : "," + extra) + "}";

    public static string App(string id = "viewer", string name = "Example Viewer", string? icon = null, params string[] downloads)
    {
        var parts = new List<string> { $"\"id\":\"{id}\"", $"\"name\":{System.Text.Json.JsonSerializer.Serialize(name)}" };
        if (icon != null) parts.Add(icon);
        parts.Add($"\"downloads\":[{string.Join(",", downloads.Length == 0 ? [Download()] : downloads)}]");
        return "{" + string.Join(",", parts) + "}";
    }

    public static string Manifest(params string[] apps) =>
        $$"""{"schemaVersion":2,"release":"1.0.0","apps":[{{string.Join(",", apps)}}]}""";

    public static AppEntry? Find(this AppsCatalog catalog, string id) => catalog.Apps.FirstOrDefault(a => a.Id == id);
}

public class AppsManifestTests
{
    private static AppsCatalog Parse(params string[] apps) => AppsManifest.Parse(ManifestJson.Manifest(apps));

    private static AppEntry Single(params string[] downloads) =>
        Parse(ManifestJson.App(downloads: downloads)).Apps.Should().ContainSingle().Subject;

    [Fact]
    public void Parse_ExampleFixture_ReadsTheAppItsIconAndItsDownloadsInOrder()
    {
        var catalog = AppsManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Apps", "manifest.json")));

        catalog.Reachable.Should().BeTrue();
        var app = catalog.Apps.Should().ContainSingle().Subject;
        app.Id.Should().Be("example-viewer");
        app.Name.Should().Be("Example Viewer");
        app.Icon.Should().Be("example-viewer.png");
        app.Downloads.Should().Equal(
            new AppDownload(AppPlatform.Windows, AppArchitecture.X64, null, false, new AppFile("2.1.0", "ExampleViewer-2.1.0.msi", 48234496), "windows.md"),
            new AppDownload(AppPlatform.Linux, AppArchitecture.Arm64, "Kiosk devices", false, new AppFile("2.1.0", "example-viewer_2.1.0_arm64.deb", 292076), "linux.md"),
            new AppDownload(AppPlatform.Android, AppArchitecture.Arm64, null, true, new AppFile("2.1.0-preview", "ExampleViewer-2.1.0-preview.apk", 2432848), "android.md"));
        catalog.Files.Should().HaveCount(3);
        catalog.Icons.Should().Equal("example-viewer.png");
    }

    [Fact]
    public void Parse_DownloadWithoutFileOrGuide_IsListedWithNeither()
    {
        Single(ManifestJson.Download(extra: null)).Downloads.Should().Equal(
            new AppDownload(AppPlatform.Windows, AppArchitecture.X64, null, false, null, null));
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
    [InlineData("windows", AppPlatform.Windows)]
    [InlineData("linux", AppPlatform.Linux)]
    [InlineData("macos", AppPlatform.Macos)]
    [InlineData("android", AppPlatform.Android)]
    [InlineData("ios", AppPlatform.Ios)]
    public void Parse_EveryKnownPlatform_IsRead(string platform, AppPlatform expected)
    {
        Single(ManifestJson.Download(platform: platform)).Downloads.Should().ContainSingle().Which.Platform.Should().Be(expected);
    }

    [Theory]
    [InlineData("x64", AppArchitecture.X64)]
    [InlineData("arm64", AppArchitecture.Arm64)]
    [InlineData("x86", AppArchitecture.X86)]
    [InlineData("arm", AppArchitecture.Arm)]
    [InlineData("any", AppArchitecture.Any)]
    public void Parse_EveryKnownArchitecture_IsRead(string architecture, AppArchitecture expected)
    {
        Single(ManifestJson.Download(architecture: architecture)).Downloads.Should().ContainSingle().Which.Architecture.Should().Be(expected);
    }

    [Theory]
    [InlineData("\"platform\":\"beos\",\"architecture\":\"x64\"")]
    [InlineData("\"platform\":\"Windows\",\"architecture\":\"x64\"")]
    [InlineData("\"platform\":\"windows\",\"architecture\":\"riscv64\"")]
    [InlineData("\"platform\":\"windows\",\"architecture\":\"X64\"")]
    [InlineData("\"platform\":\"windows\"")]
    [InlineData("\"architecture\":\"x64\"")]
    [InlineData("\"platform\":1,\"architecture\":\"x64\"")]
    public void Parse_UnknownPlatformOrArchitecture_DropsThatDownloadOnly(string members)
    {
        var app = Single("{" + members + "}", ManifestJson.Download(platform: "linux", extra: null));

        app.Downloads.Select(d => d.Platform).Should().Equal(AppPlatform.Linux);
    }

    [Fact]
    public void Parse_LabelAndPreview_AreRead()
    {
        var download = Single(ManifestJson.Download(extra: "\"label\":\"Kiosk devices\",\"preview\":true")).Downloads.Single();

        download.Label.Should().Be("Kiosk devices");
        download.Preview.Should().BeTrue();
    }

    [Theory]
    [InlineData("\"label\":\"\"")]
    [InlineData("\"label\":\"line\\nbreak\"")]
    [InlineData("\"label\":7")]
    [InlineData("\"preview\":\"yes\"")]
    [InlineData("\"preview\":1")]
    public void Parse_InvalidLabelOrPreview_DropsThatDownload(string member)
    {
        Single(ManifestJson.Download(extra: member), ManifestJson.Download(platform: "linux", extra: null))
            .Downloads.Select(d => d.Platform).Should().Equal(AppPlatform.Linux);
    }

    [Fact]
    public void Parse_LabelOverForty_DropsThatDownload_AtFortyKeepsIt()
    {
        Single(ManifestJson.Download(extra: $"\"label\":\"{new string('x', 41)}\"")).Downloads.Should().BeEmpty();
        Single(ManifestJson.Download(extra: $"\"label\":\"{new string('x', 40)}\"")).Downloads.Should().ContainSingle();
    }

    [Fact]
    public void Parse_SamePlatformArchitectureAndLabel_KeepsTheFirst_ADifferentLabelIsAnotherDownload()
    {
        var app = Single(
            ManifestJson.Download(extra: "\"version\":\"1\",\"file\":\"a.exe\",\"size\":1"),
            ManifestJson.Download(extra: "\"version\":\"2\",\"file\":\"b.exe\",\"size\":1"),
            ManifestJson.Download(extra: "\"label\":\"Kiosk\",\"version\":\"3\",\"file\":\"c.exe\",\"size\":1"));

        app.Downloads.Select(d => d.File!.FileName).Should().Equal("a.exe", "c.exe");
    }

    [Fact]
    public void Parse_MoreThanEightDownloads_ReadsOnlyTheFirstEight()
    {
        var downloads = Enumerable.Range(0, 12).Select(i => ManifestJson.Download(extra: $"\"label\":\"v{i}\"")).ToArray();

        var app = Single(downloads);

        app.Downloads.Should().HaveCount(AppsManifest.MaxDownloads);
        app.Downloads[^1].Label.Should().Be("v7");
    }

    [Fact]
    public void Parse_MoreThanSixteenApps_ReadsOnlyTheFirstSixteen()
    {
        var apps = Enumerable.Range(0, 40).Select(i => ManifestJson.App(id: $"app-{i}", downloads: ManifestJson.Download(extra: null))).ToArray();

        var catalog = Parse(apps);

        catalog.Apps.Should().HaveCount(AppsManifest.MaxApps);
        catalog.Apps[^1].Id.Should().Be("app-15");
    }

    [Fact]
    public void Parse_DuplicateAppId_KeepsTheFirst()
    {
        var catalog = Parse(ManifestJson.App(name: "First"), ManifestJson.App(name: "Second", downloads: ManifestJson.Download(extra: null)));

        catalog.Apps.Should().ContainSingle().Which.Name.Should().Be("First");
    }

    [Fact]
    public void Parse_FileNameUsedByAnEarlierApp_DropsTheLaterDownload()
    {
        var catalog = Parse(ManifestJson.App(id: "a"), ManifestJson.App(id: "b"));

        catalog.Find("a")!.Downloads.Should().ContainSingle();
        catalog.Find("b")!.Downloads.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("-viewer")]
    [InlineData("view er")]
    [InlineData("viewer\n")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    [InlineData("")]
    public void Parse_InvalidId_DropsTheApp(string id)
    {
        Parse(ManifestJson.App(id: id)).Apps.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NameOverTheLimit_DropsTheApp_AtTheLimitKeepsIt()
    {
        Parse(ManifestJson.App(name: new string('x', 81))).Apps.Should().BeEmpty();
        Parse(ManifestJson.App(name: new string('x', 80))).Apps.Should().ContainSingle();
    }

    public static TheoryData<string> HostileNames() =>
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
    [MemberData(nameof(HostileNames))]
    public void Parse_NameThatIsNotPlainText_DropsTheApp(string name)
    {
        Parse(ManifestJson.App(name: name), ManifestJson.App(id: "other", downloads: ManifestJson.Download(extra: null)))
            .Apps.Select(a => a.Id).Should().Equal("other");
    }

    [Fact]
    public void Parse_EscapedLoneSurrogate_DropsTheAppWithoutThrowing()
    {
        const string manifest = """{"schemaVersion":2,"apps":[{"id":"viewer","name":"bad \udc00 name","downloads":[]},{"id":"other","name":"Fine","downloads":[]}]}""";

        AppsManifest.Parse(manifest).Apps.Select(a => a.Id).Should().Equal("other");
    }

    [Fact]
    public void Parse_NonAsciiName_IsKept()
    {
        Parse(ManifestJson.App(name: "Przeglądarka – wersja ✓")).Find("viewer")!.Name.Should().Be("Przeglądarka – wersja ✓");
    }

    [Theory]
    [InlineData("\"downloads\":\"nope\"")]
    [InlineData("\"downloads\":null")]
    public void Parse_DownloadsNotAnArray_DropsTheApp(string downloads)
    {
        AppsManifest.Parse($$"""{"schemaVersion":2,"apps":[{"id":"viewer","name":"V",{{downloads}}}]}""").Apps.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoDownloads_KeepsTheApp()
    {
        AppsManifest.Parse("""{"schemaVersion":2,"apps":[{"id":"viewer","name":"V","downloads":[]}]}""")
            .Apps.Should().ContainSingle().Which.Downloads.Should().BeEmpty();
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
    public void Parse_IncompleteOrInvalidFileFields_DropsThatDownload(string fileFields)
    {
        Single(ManifestJson.Download(extra: fileFields)).Downloads.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"guide\":\"viewer.txt\"")]
    [InlineData("\"guide\":\"../viewer.md\"")]
    [InlineData("\"guide\":\".md\"")]
    [InlineData("\"guide\":7")]
    [InlineData("\"guide\":\"a b.md\"")]
    public void Parse_InvalidGuideName_DropsThatDownload(string guide)
    {
        Single(ManifestJson.Download(extra: guide)).Downloads.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"icon\":\"viewer.svg\"")]
    [InlineData("\"icon\":\"viewer.png.svg\"")]
    [InlineData("\"icon\":\"../viewer.png\"")]
    [InlineData("\"icon\":\".png\"")]
    [InlineData("\"icon\":\"a b.png\"")]
    [InlineData("\"icon\":\"viewer.PNG\"")]
    [InlineData("\"icon\":7")]
    public void Parse_InvalidIconName_KeepsTheAppWithoutAnIcon(string icon)
    {
        var catalog = Parse(ManifestJson.App(icon: icon));

        catalog.Apps.Should().ContainSingle().Which.Icon.Should().BeNull();
        catalog.Icons.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WrongJsonTypes_DropsThoseAppsWithoutThrowing()
    {
        const string manifest = """{"schemaVersion":2,"release":5,"apps":[7,null,"x",[],{"id":1,"name":2},{"id":"ok","name":"Fine","downloads":[7,null]}]}""";

        var catalog = AppsManifest.Parse(manifest);

        catalog.Reachable.Should().BeTrue();
        catalog.Apps.Should().ContainSingle().Which.Downloads.Should().BeEmpty();
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

    [Fact]
    public void IsPlainText_LoneSurrogate_IsRefused()
    {
        AppsManifest.IsPlainText("lone" + (char)0xD800, 80).Should().BeFalse();
        AppsManifest.IsPlainText("pair" + char.ConvertFromUtf32(0x1F600), 80).Should().BeTrue();
    }
}

public class AppIconTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void IsPng_OnlyThePngSignatureWithinTheCap()
    {
        AppIcon.IsPng([.. Signature, 1, 2]).Should().BeTrue();
        AppIcon.IsPng([.. Signature, .. new byte[AppIcon.MaxBytes - Signature.Length]]).Should().BeTrue();
        AppIcon.IsPng([.. Signature, .. new byte[AppIcon.MaxBytes]]).Should().BeFalse();
        AppIcon.IsPng("<svg onload=\"alert(1)\"/>"u8).Should().BeFalse();
        AppIcon.IsPng(Signature[..7]).Should().BeFalse();
        AppIcon.IsPng([]).Should().BeFalse();
    }

    [Fact]
    public void ExampleIcon_InThePackaging_IsAPng()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "packaging", "apps", "example", "example-viewer.png");

        AppIcon.IsPng(File.ReadAllBytes(path)).Should().BeTrue();
    }

    [Fact]
    public void Path_IsRelativeToThePage_AndCarriesTheVersion()
    {
        AppIcon.Path("example-viewer.png", "0123abcd").Should().Be("apps/icons/example-viewer.png?v=0123abcd");
    }

    [Fact]
    public void Version_IsAShortHashOfTheBytes_ThatChangesWithThem()
    {
        var version = AppIcon.Version([.. Signature, 1]);

        version.Should().MatchRegex("^[0-9a-f]{16}$");
        AppIcon.Version([.. Signature, 1]).Should().Be(version);
        AppIcon.Version([.. Signature, 2]).Should().NotBe(version);
    }
}

public class AppsCatalogClientTests
{
    private static readonly AppsOptions Options = new(new Uri("http://apps.test:8095/"));
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly string Manifest = ManifestJson.Manifest(ManifestJson.App(downloads: ManifestJson.Download(extra: "\"version\":\"1\",\"file\":\"a.apk\",\"size\":1,\"guide\":\"viewer.md\"")));

    internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
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

        catalog.Files.Single().FileName.Should().Be("a.apk");
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

        (await client.GetGuideAsync("viewer.md"))!.Text.Should().Be("1. Install it.");
        handler.Paths.Should().Equal("/guides/viewer.md");
    }

    [Fact]
    public async Task GetGuideAsync_OverTheCap_IsUnavailable()
    {
        var (client, _) = Client((_, _) => Task.FromResult(Ok(Enumerable.Repeat((byte)'a', AppGuideMarkdown.MaxBytes + 1).ToArray())));

        (await client.GetGuideAsync("viewer.md")).Should().BeNull();
    }

    [Fact]
    public async Task GetGuideAsync_DeclaredOverTheCap_IsUnavailable_WithoutReadingTheBody()
    {
        var body = new CountingStream(new MemoryStream(new byte[AppGuideMarkdown.MaxBytes + 1]));
        var (client, _) = Client((_, _) =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = AppGuideMarkdown.MaxBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

        (await client.GetGuideAsync("viewer.md")).Should().BeNull();
        body.BytesRead.Should().Be(0);
    }

    [Fact]
    public async Task GetGuideAsync_BodyLongerThanDeclared_IsUnavailable()
    {
        var (client, _) = Client((_, _) =>
        {
            var content = new StreamContent(new MemoryStream(Enumerable.Repeat((byte)'a', AppGuideMarkdown.MaxBytes + 1).ToArray()));
            content.Headers.ContentLength = 10;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

        (await client.GetGuideAsync("viewer.md")).Should().BeNull();
    }

    [Fact]
    public async Task GetGuideAsync_BodyOfUnknownLength_IsReadWhole()
    {
        var text = string.Concat(Enumerable.Repeat("1. Step.\n", 1500));
        var (client, _) = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(text))),
        }));

        (await client.GetGuideAsync("viewer.md"))!.Text.Should().Be(text);
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); BytesRead += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body with no known length, as a chunked response has.</summary>
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 1000));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
    public async Task GetIconAsync_Png_IsReturnedFromUnderIcons()
    {
        var (client, handler) = Client((_, _) => Task.FromResult(Ok(Png)));

        (await client.GetIconAsync("viewer.png")).Should().Equal(Png);
        handler.Paths.Should().Equal("/icons/viewer.png");
    }

    [Fact]
    public async Task GetIconAsync_NotAPngOrOverTheCap_IsNull()
    {
        var (svg, _) = Client((_, _) => Task.FromResult(Ok("<svg onload=\"alert(1)\"/>")));
        var (huge, _) = Client((_, _) => Task.FromResult(Ok([.. Png, .. new byte[AppIcon.MaxBytes]])));

        (await svg.GetIconAsync("viewer.png")).Should().BeNull();
        (await huge.GetIconAsync("viewer.png")).Should().BeNull();
    }

    [Theory]
    [InlineData("viewer.svg")]
    [InlineData("../manifest.png")]
    public async Task GetIconAsync_NameThatIsNotAnIcon_IsNeverRequested(string name)
    {
        var (client, handler) = Client((_, _) => Task.FromResult(Ok(Png)));

        (await client.GetIconAsync(name)).Should().BeNull();
        handler.Paths.Should().BeEmpty();
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

        var client = new AppsCatalogClient(AppsCatalogClient.CreateHttpClient(AppsCatalogClient.MaxConnections), new AppsOptions(new Uri($"http://127.0.0.1:{port}/")));

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
    private static AppsSettings Settings(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
        .Build()
        .AppsSettings();

    [Fact]
    public void NothingConfigured_IsDisabledWithoutError()
    {
        var settings = Settings();

        settings.Enabled.Should().BeFalse();
        settings.Error.Should().BeNull();
    }

    [Fact]
    public void DisabledExplicitly_IgnoresTheRest()
    {
        Settings(("Apps:Enabled", "false"), ("Apps:SourceUrl", "nope")).Should().Be(AppsSettings.Disabled);
    }

    [Fact]
    public void EnabledWithASourceUrl_UsesNeutralDefaults()
    {
        var settings = Settings(("Apps:Enabled", "true"), ("Apps:SourceUrl", "http://172.17.0.1:8095"));

        settings.Enabled.Should().BeTrue();
        settings.Options.Should().Be(new AppsOptions(new Uri("http://172.17.0.1:8095/"), null, "Apps"));
    }

    [Fact]
    public void EnabledWithEverything_ReadsTitleAndPackage()
    {
        var options = Settings(("Apps:Enabled", "True"), ("Apps:SourceUrl", "https://apps.local/base"),
            ("Apps:Title", "Downloads"), ("Apps:PackageName", "example-apps")).Options!;

        options.SourceUrl.Should().Be(new Uri("https://apps.local/base/"));
        options.Title.Should().Be("Downloads");
        options.PackageName.Should().Be("example-apps");
        options.FileUrl("a.apk").Should().Be(new Uri("https://apps.local/base/files/a.apk"));
    }

    [Fact]
    public void EachValue_IsReadByItsOwnExtension()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Apps:Enabled"] = "true", ["Apps:SourceUrl"] = "http://h/", ["Apps:Title"] = "T", ["Apps:PackageName"] = "p",
        }).Build();

        configuration.AppsEnabled().Should().Be("true");
        configuration.AppsSourceUrl().Should().Be("http://h/");
        configuration.AppsTitle().Should().Be("T");
        configuration.AppsPackageName().Should().Be("p");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("ftp://host/")]
    [InlineData("file:///srv/apps/")]
    [InlineData("/relative/")]
    [InlineData("http://user:pass@host/")]
    [InlineData("http://host/?q=1")]
    public void EnabledWithoutAUsableSourceUrl_StaysOffAndSaysWhy(string? url)
    {
        var values = new List<(string, string)> { ("Apps:Enabled", "true") };
        if (url != null) values.Add(("Apps:SourceUrl", url));

        var settings = Settings(values.ToArray());

        settings.Enabled.Should().BeFalse();
        settings.Error.Should().Contain("Apps:SourceUrl");
    }

    [Fact]
    public void EnabledNotABoolean_StaysOffAndSaysWhy()
    {
        var settings = Settings(("Apps:Enabled", "yes"));

        settings.Enabled.Should().BeFalse();
        settings.Error.Should().Contain("Apps:Enabled");
    }

    [Fact]
    public void TitleThatIsNotPlainText_StaysOffAndSaysWhy()
    {
        var settings = Settings(("Apps:Enabled", "true"), ("Apps:SourceUrl", "http://h/"), ("Apps:Title", new string('x', 41)));

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

public class AppCardTests
{
    private static readonly AppFile File = new("1", "a.exe", 1);
    private static readonly AppDownload WithFile = new(AppPlatform.Windows, AppArchitecture.X64, null, false, File, "viewer.md");
    private static readonly AppDownload WithoutFile = new(AppPlatform.Ios, AppArchitecture.Any, null, false, null, null);
    private static readonly AppEntry Viewer = new("viewer", "Example Viewer", "viewer.png", [WithFile, WithoutFile]);
    private static readonly AppEntry Other = new("other", "Other", null, []);
    private static readonly AppsCatalog Listed = new(true, [Viewer, Other]);
    private static readonly AppsPackageSnapshot Deployed = new(true, true, false);

    [Fact]
    public void Decide_File_IsReady_NoFile_IsNotAvailableYet()
    {
        DownloadRow.Decide(File, Deployed).Should().Be(AppStatus.Ready);
        DownloadRow.Decide(null, Deployed).Should().Be(AppStatus.NotAvailableYet);
    }

    [Fact]
    public void Decide_UpdateRunning_WinsOverEverything()
    {
        DownloadRow.Decide(File, new AppsPackageSnapshot(true, true, true)).Should().Be(AppStatus.Downloading);
    }

    [Fact]
    public void For_ReadableCatalog_OneCardPerAppInOrder_OneRowPerDownload()
    {
        var cards = AppCard.For(Listed, Deployed);

        cards.Select(c => c.Id).Should().Equal("viewer", "other");
        cards[0].Downloads.Select(r => r.CanDownload).Should().Equal(true, false);
        cards[0].Downloads.Select(r => r.Guide).Should().Equal(LoadState.Loading, LoadState.None);
        cards[0].Icon.Should().Be(LoadState.Loading);
        cards[1].Icon.Should().Be(LoadState.None);
        cards[1].Downloads.Should().BeEmpty();
    }

    [Fact]
    public void For_UnreadableCatalog_InventsNoCards()
    {
        AppCard.For(null, Deployed).Should().BeEmpty();
        AppCard.For(AppsCatalog.Unreachable, Deployed).Should().BeEmpty();
        AppCard.For(AppsCatalog.Unsupported("1"), Deployed).Should().BeEmpty();
    }

    [Fact]
    public void Apply_UpdateStarts_EveryRowIsDownloading_AndSaysSo()
    {
        var card = new AppCard(Viewer, Deployed);
        var changed = new List<string?>();
        card.Downloads[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        card.Apply(new AppsPackageSnapshot(true, true, true));

        card.Downloads.Select(r => r.Status).Should().Equal(AppStatus.Downloading, AppStatus.Downloading);
        changed.Should().Equal(nameof(DownloadRow.Status));
    }

    [Fact]
    public void SetGuide_NullIsUnavailable_AGuideIsLoaded()
    {
        var unavailable = new DownloadRow(WithFile);
        var loaded = new DownloadRow(WithFile);
        var guide = AppGuideMarkdown.Decode("1. Install."u8)!;

        unavailable.SetGuide(null);
        loaded.SetGuide(guide);

        unavailable.Guide.Should().Be(LoadState.Unavailable);
        loaded.Guide.Should().Be(LoadState.Loaded);
        loaded.GuideContent.Should().BeSameAs(guide);
    }

    [Fact]
    public void SetIcon_NullIsUnavailable_AVersionIsLoaded()
    {
        var unavailable = new AppCard(Viewer, Deployed);
        var loaded = new AppCard(Viewer, Deployed);

        unavailable.SetIcon(null);
        loaded.SetIcon("0123abcd");

        unavailable.Icon.Should().Be(LoadState.Unavailable);
        loaded.Icon.Should().Be(LoadState.Loaded);
        loaded.IconVersion.Should().Be("0123abcd");
    }

    [Theory]
    [InlineData(true, false, AppsPageStatus.NotDownloaded)]
    [InlineData(true, true, AppsPageStatus.Unavailable)]
    [InlineData(false, false, AppsPageStatus.Unavailable)]
    public void DecidePage_ImageNotAnswering_DependsOnThePackage(bool registered, bool deployed, AppsPageStatus expected)
    {
        AppCard.DecidePage(AppsCatalog.Unreachable, new AppsPackageSnapshot(registered, deployed, false)).Should().Be(expected);
    }

    [Fact]
    public void DecidePage_CoversCheckingUnsupportedEmptyAndListed()
    {
        AppCard.DecidePage(null, Deployed).Should().Be(AppsPageStatus.Checking);
        AppCard.DecidePage(AppsCatalog.Unsupported("1"), Deployed).Should().Be(AppsPageStatus.UnsupportedSchema);
        AppCard.DecidePage(new AppsCatalog(true, []), Deployed).Should().Be(AppsPageStatus.Empty);
        AppCard.DecidePage(Listed, Deployed).Should().Be(AppsPageStatus.Listed);
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
