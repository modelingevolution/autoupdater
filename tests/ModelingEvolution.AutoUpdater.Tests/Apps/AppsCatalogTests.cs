using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using System.Net.Sockets;
using ModelingEvolution.AutoUpdater.Apps;
using ModelingEvolution.AutoUpdater.Extensions;

namespace ModelingEvolution.AutoUpdater.Tests.Apps;

public class AppsManifestTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Manifest(string file = "setup.exe", string sha = Sha, string version = "1.2.3", long size = 10, string platform = "windows") =>
        $$"""{"schemaVersion":1,"release":"1.0.0","apps":[{"platform":"{{platform}}","version":"{{version}}","file":"{{file}}","size":{{size}},"sha256":"{{sha}}"}]}""";

    private const string AllVerified = """{"state":"verified","blocked":[]}""";

    [Fact]
    public void Parse_ValidDocuments_ReturnsVerifiedFile()
    {
        var catalog = AppsManifest.Parse(Manifest(), AllVerified);

        catalog.Reachable.Should().BeTrue();
        catalog.Release.Should().Be("1.0.0");
        catalog.Find("windows").Should().Be(new AppFile("windows", "1.2.3", "setup.exe", 10, Sha, true));
    }

    [Fact]
    public void Parse_FileBlockedByTheContainer_IsNotVerified()
    {
        var catalog = AppsManifest.Parse(Manifest(), """{"state":"failed","blocked":["setup.exe"]}""");

        catalog.Find("windows")!.Verified.Should().BeFalse();
    }

    [Fact]
    public void Parse_OtherFileBlocked_ThisOneStaysVerified()
    {
        var catalog = AppsManifest.Parse(Manifest(), """{"state":"failed","blocked":["other.apk"]}""");

        catalog.Find("windows")!.Verified.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"state":"unknown","blocked":[]}""")]
    [InlineData("""{"state":"verified"}""")]
    public void Parse_NoUsableVerification_FailsClosed(string? verification)
    {
        var catalog = AppsManifest.Parse(Manifest(), verification);

        catalog.Find("windows")!.Verified.Should().BeFalse();
        catalog.VerificationKnown.Should().BeFalse();
    }

    [Fact]
    public void Parse_ContainerReportedItsCheck_VerificationIsKnown()
    {
        AppsManifest.Parse(Manifest(), """{"state":"failed","blocked":["setup.exe"]}""").VerificationKnown.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>not json</html>")]
    [InlineData("[]")]
    [InlineData("""{"apps":"nope"}""")]
    [InlineData("{")]
    public void Parse_BrokenManifest_IsUnreachableAndDoesNotThrow(string? manifest)
    {
        AppsManifest.Parse(manifest, AllVerified).Should().Be(AppsCatalog.Unreachable);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("a/b.exe")]
    [InlineData("a b.exe")]
    [InlineData("a%2fb.exe")]
    [InlineData("<script>.exe")]
    public void Parse_UnsafeFileName_DropsTheEntry(string file)
    {
        AppsManifest.Parse(Manifest(file: file), AllVerified).Files.Should().BeEmpty();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Parse_MalformedSha256_DropsTheEntry(string sha)
    {
        AppsManifest.Parse(Manifest(sha: sha), AllVerified).Files.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Parse_NonPositiveSize_DropsTheEntry(long size)
    {
        AppsManifest.Parse(Manifest(size: size), AllVerified).Files.Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnknownPlatform_DropsTheEntry()
    {
        AppsManifest.Parse(Manifest(platform: "beos"), AllVerified).Files.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WrongJsonTypesInAnEntry_DropsItWithoutThrowing()
    {
        const string manifest = """{"release":5,"apps":[7,{"platform":1,"version":2,"file":3,"size":"x","sha256":4}]}""";

        var catalog = AppsManifest.Parse(manifest, AllVerified);

        catalog.Reachable.Should().BeTrue();
        catalog.Files.Should().BeEmpty();
        catalog.Release.Should().BeNull();
    }
}

public class AppsCatalogClientTests
{
    private static readonly AppsOptions Options = new("rocketwelder-apps", 8095, new Uri("http://apps.test:8095/"));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private static AppsCatalogClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new StubHandler(respond)), Options);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task GetAsync_ContainerAnswers_ReadsBothDocuments()
    {
        var sha = new string('b', 64);
        var client = Client((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/manifest.json" => Ok($$"""{"release":"2.0.0","apps":[{"platform":"android","version":"1","file":"a.apk","size":1,"sha256":"{{sha}}"}]}"""),
            "/verification.json" => Ok("""{"state":"verified","blocked":[]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        var catalog = await client.GetAsync();

        catalog.Find("android")!.Verified.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_ConnectionRefused_IsUnreachable()
    {
        var client = Client((_, _) => throw new HttpRequestException("refused"));

        (await client.GetAsync()).Should().Be(AppsCatalog.Unreachable);
    }

    [Fact]
    public async Task GetAsync_NotFound_IsUnreachable()
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        (await client.GetAsync()).Should().Be(AppsCatalog.Unreachable);
    }

    [Fact]
    public async Task GetAsync_ContainerHangs_GivesUpWithinItsTimeout()
    {
        var client = Client(async (_, cancellationToken) =>
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
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024))))
        }));

        (await client.GetAsync()).Should().Be(AppsCatalog.Unreachable);
    }

    [Fact]
    public async Task GetAsync_ContainerRedirects_DoesNotFollowAndIsUnreachable()
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

        var client = new AppsCatalogClient(AppsCatalogClient.CreateHttpClient(),
            new AppsOptions("rocketwelder-apps", 8095, new Uri($"http://127.0.0.1:{port}/")));

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

    [Fact]
    public async Task GetAsync_ManifestOkButNoVerification_OffersNothingAsVerified()
    {
        var sha = new string('c', 64);
        var client = Client((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/manifest.json"
            ? Ok($$"""{"apps":[{"platform":"linux","version":"1","file":"a.deb","size":1,"sha256":"{{sha}}"}]}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound)));

        (await client.GetAsync()).Find("linux")!.Verified.Should().BeFalse();
    }
}

public class AppsOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
        .Build();

    [Fact]
    public void From_NothingConfigured_UsesTheComposePackageDefaults()
    {
        var options = AppsOptions.From(Config());

        options.PackageName.Should().Be("rocketwelder-apps");
        options.Port.Should().Be(8095);
        options.InternalBaseUrl.Should().Be(new Uri("http://172.17.0.1:8095/"));
    }

    [Fact]
    public void From_SshHostSet_ReadsTheManifestThroughThatHost()
    {
        AppsOptions.From(Config(("SshHost", "10.0.0.9"))).InternalBaseUrl.Should().Be(new Uri("http://10.0.0.9:8095/"));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("0")]
    [InlineData("70000")]
    public void From_InvalidPort_FallsBackToTheDefault(string port)
    {
        AppsOptions.From(Config(("Apps:Port", port))).Port.Should().Be(8095);
    }

    [Theory]
    [InlineData("http://station.local:8080/apps", "http://station.local:8095/files/a.apk")]
    [InlineData("https://10.1.2.3/", "http://10.1.2.3:8095/files/a.apk")]
    public void DownloadUrl_UsesTheHostThePersonReachedThePageOn(string page, string expected)
    {
        AppsOptions.From(Config()).DownloadUrl(new Uri(page), "a.apk").Should().Be(expected);
    }
}

public class AppRowTests
{
    private static readonly AppFile Good = new("windows", "1", "a.exe", 1, new string('a', 64), true);
    private static readonly AppsCatalog WithGood = new(true, "1.0.0", [Good]);
    private static readonly AppsPackageSnapshot Deployed = new(true, false);

    [Fact]
    public void Decide_VerifiedFile_IsReady()
    {
        AppRow.Decide(WithGood, Good, Deployed).Should().Be(AppStatus.Ready);
    }

    [Fact]
    public void Decide_FileFailedVerification_IsChecksumFailed()
    {
        AppRow.Decide(WithGood, Good with { Verified = false }, Deployed).Should().Be(AppStatus.ChecksumFailed);
    }

    [Fact]
    public void Decide_ContainerGaveNoVerificationResult_IsNotVerified()
    {
        var unknown = WithGood with { VerificationKnown = false };

        AppRow.Decide(unknown, Good with { Verified = false }, Deployed).Should().Be(AppStatus.NotVerified);
    }

    [Fact]
    public void Decide_PlatformNotInRelease_IsNotAvailableYet()
    {
        AppRow.Decide(new AppsCatalog(true, "1.0.0", []), null, Deployed).Should().Be(AppStatus.NotAvailableYet);
    }

    [Fact]
    public void Decide_DeployedButContainerNotAnswering_IsUnavailable()
    {
        AppRow.Decide(AppsCatalog.Unreachable, null, Deployed).Should().Be(AppStatus.ServiceUnavailable);
    }

    [Fact]
    public void Decide_NeverDeployed_IsNotDownloaded()
    {
        AppRow.Decide(AppsCatalog.Unreachable, null, new AppsPackageSnapshot(false, false)).Should().Be(AppStatus.NotDownloaded);
    }

    [Fact]
    public void Decide_UpdateRunning_WinsOverEverything()
    {
        AppRow.Decide(WithGood, Good, new AppsPackageSnapshot(true, true)).Should().Be(AppStatus.Downloading);
    }

    [Fact]
    public void Apply_OnlyAReadyFileCanBeDownloaded()
    {
        var row = new AppRow(AppGuides.All.First(g => g.Platform == AppGuides.Windows));

        row.Apply(new AppsCatalog(true, "1.0.0", [Good with { Verified = false }]), Deployed);
        row.CanDownload.Should().BeFalse();

        row.Apply(WithGood, Deployed);
        row.CanDownload.Should().BeTrue();
    }
}

public class AppGuidesTests
{
    [Fact]
    public void All_CoversTheThreePlatforms()
    {
        AppGuides.All.Select(g => g.Platform).Should().BeEquivalentTo("windows", "linux", "android");
    }

    [Fact]
    public void Android_IsLabelledPreview_AndWarnsThatUninstallErasesSettings()
    {
        var android = AppGuides.All.Single(g => g.Platform == AppGuides.Android);

        android.Preview.Should().BeTrue();
        android.Warning.Should().Contain("erases");
    }

    [Fact]
    public void Windows_SaysSmartScreenWarnsAndHowToContinue()
    {
        var windows = AppGuides.All.Single(g => g.Platform == AppGuides.Windows);

        windows.Note.Should().Contain("SmartScreen");
        windows.Steps.Should().Contain(s => s.Text.Contains("More info") && s.Text.Contains("Run anyway"));
    }

    [Fact]
    public void Linux_WarnsItIsForDedicatedKioskDevicesAndReboots()
    {
        var linux = AppGuides.All.Single(g => g.Platform == AppGuides.Linux);

        linux.Warning.Should().Contain("Dedicated kiosk").And.Contain("reboots");
    }

    [Fact]
    public void AllText_HasNoInternalHostsAddressesOrCustomerNames()
    {
        var text = string.Join(" ", AppGuides.All.SelectMany(g =>
            new[] { g.DisplayName, g.Requirements, g.Warning, g.Note, g.VerifyHashCommand }.Concat(g.Steps.Select(s => s.Text))));

        text.Should().NotMatchRegex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}");
        text.Should().NotContainAny("modelingevolution", "saturn", "Pamet", "Pamet".ToLowerInvariant(), "hyperion");
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
    [InlineData("Error response from daemon: Head \"https://docker.example.test/v2/rocketwelder/rocketwelder-apps/manifests/1.2.0\": unauthorized: authentication required", Credentials)]
    [InlineData("failed to authorize: failed to fetch oauth token: unexpected status: 401 Unauthorized", Credentials)]
    [InlineData("unauthorized: authentication required (repository not found)", Credentials)]
    [InlineData("Error response from daemon: manifest for docker.example.test/rocketwelder/rocketwelder-apps:1.2.0 not found: manifest unknown: manifest unknown", NotFound)]
    [InlineData("docker.example.test/rocketwelder/rocketwelder-apps:1.2.0: manifest unknown", NotFound)]
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
    [InlineData("open /data/rocketwelder-apps/.env: permission denied")]
    [InlineData("deploy@station: Permission denied (publickey).")]
    [InlineData("git@github.com:modelingevolution/rocketwelder-apps-compose.git: Permission denied (publickey)")]
    [InlineData("bash: docker: command not found")]
    [InlineData("sudo: up-1.2.0.sh: command not found")]
    [InlineData("network rocketwelder_default not found")]
    [InlineData("Error response from daemon: network 3f2a9c not found")]
    [InlineData("no such service: rocketwelder-apps")]
    [InlineData("SSH command timed out after 300 seconds: docker compose up -d")]
    [InlineData("ssh: connect to host neuron-7 port 22: Connection timed out")]
    [InlineData("ssh: connect to host neuron-7 port 22: Connection refused")]
    [InlineData("dial tcp 192.168.30.29:443: i/o timeout")]
    [InlineData("Error response from daemon: pull access denied for x, repository does not exist or may require 'docker login'")]
    [InlineData("failed to pull docker.example.test/rocketwelder/rocketwelder-apps:1.2.0: denied")]
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
    [InlineData("dial tcp: lookup saturn on 127.0.0.11:53: no such host", "saturn")]
    [InlineData("dial tcp [fd00::1:2]:443: connect: connection refused", "fd00")]
    [InlineData("ssh: connect to host fe80::1%eth0 port 22: Connection timed out", "fe80")]
    [InlineData("git@github.com:modelingevolution/rocketwelder-apps-compose.git: Permission denied", "modelingevolution")]
    [InlineData("ssh: connect to host neuron-7 port 22: Connection refused", "neuron-7")]
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
    public void Extensions_NothingConfigured_ReturnTheDefaults()
    {
        var config = Config();

        config.DockerHostAddress().Should().Be(ModelingEvolution.AutoUpdater.Extensions.ConfigurationExtensions.DefaultDockerHost).And.Be("172.17.0.1");
        config.AppsPackageName().Should().Be("rocketwelder-apps");
        config.AppsPort().Should().Be(8095);
        config.AppsInternalBaseUrl().Should().Be(new Uri("http://172.17.0.1:8095/"));
    }

    [Fact]
    public void DockerHostAddress_PrefersSshHostOverHostAddress()
    {
        Config(("SshHost", "10.0.0.9"), ("HostAddress", "10.0.0.8")).DockerHostAddress().Should().Be("10.0.0.9");
        Config(("HostAddress", "10.0.0.8")).DockerHostAddress().Should().Be("10.0.0.8");
    }

    [Theory]
    [InlineData("http://127.0.0.1:9000/", "http://127.0.0.1:9000/")]
    [InlineData("ftp://127.0.0.1/", "http://172.17.0.1:8095/")]
    [InlineData("not a url", "http://172.17.0.1:8095/")]
    public void AppsInternalBaseUrl_OnlyAnHttpUrlOverridesTheDefault(string configured, string expected)
    {
        Config(("Apps:InternalBaseUrl", configured)).AppsInternalBaseUrl().Should().Be(new Uri(expected));
    }
}
