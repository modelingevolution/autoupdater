using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using ModelingEvolution.AutoUpdater.Apps;
using Xunit;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

/// <summary>
/// Builds an apps image through the real publish script, with dummy files and the example guides, and runs it the way the
/// compose package does.
/// </summary>
public sealed class AppsImageFixture : IAsyncLifetime
{
    private const int ContainerPort = 8080;
    public const string WindowsFile = "ExampleViewer-1.0.0.msi";
    public const string AndroidFile = "ExampleViewer-1.0.0-arm64-v8a.apk";
    public const string LinuxFile = "example-viewer_1.0.0_arm64.deb";

    private readonly string _workDir = Directory.CreateTempSubdirectory("apps-image-").FullName;
    private IContainer _healthy = null!;
    private IContainer _scratch = null!;

    public string Tag { get; } = $"apps-test:{Guid.NewGuid():N}";
    public byte[] WindowsBytes { get; } = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
    public HttpClient Healthy { get; private set; } = null!;
    public IContainer HealthyContainer => _healthy;
    public string WindowsPath => Path.Combine(_workDir, WindowsFile);
    public string LinuxPath => Path.Combine(_workDir, LinuxFile);
    public string AndroidPath => Path.Combine(_workDir, AndroidFile);
    /// <summary>Its root filesystem is writable and its /tmp allows exec, unlike the compose-restricted one.</summary>
    public IContainer ScratchContainer => _scratch;
    public int HealthyPort => _healthy.GetMappedPublicPort(ContainerPort);
    public int ScratchPort => _scratch.GetMappedPublicPort(ContainerPort);

    public static string Guide(string name) => Path.Combine(PackagingDirectory, "example", name);
    public static string Icon => Path.Combine(PackagingDirectory, "example", "example-viewer.png");

    /// <summary>
    /// The example app with its icon and three downloads (one labelled, one preview), then a second app with one download
    /// listed without a file, in page order.
    /// </summary>
    public static string[] AppArguments(string windows, string linux, string android, string androidVersion = "1.0.0") =>
    [
        "--app", "example-viewer", "--name", "Example Viewer", "--icon", Icon,
        "--download", "--platform", "windows", "--arch", "x64", "--file", windows, "--version", "1.0.0", "--guide", Guide("windows.md"),
        "--download", "--platform", "linux", "--arch", "arm64", "--label", "Kiosk devices", "--file", linux, "--version", "1.0.0", "--guide", Guide("linux.md"),
        "--download", "--platform", "android", "--arch", "arm64", "--preview", "--file", android, "--version", androidVersion, "--guide", Guide("android.md"),
        "--app", "example-web", "--name", "Example Viewer for the web", "--download", "--platform", "ios", "--arch", "any",
    ];

    public async Task InitializeAsync()
    {
        await File.WriteAllBytesAsync(WindowsPath, WindowsBytes);
        await File.WriteAllBytesAsync(AndroidPath, RandomNumberGenerator.GetBytes(64 * 1024));
        await File.WriteAllBytesAsync(LinuxPath, RandomNumberGenerator.GetBytes(64 * 1024));

        await Publish(["--release", "1.0.0", "--local-tag", Tag, .. AppArguments(WindowsPath, LinuxPath, AndroidPath)]);

        // Same restrictions as packaging/apps/compose/docker-compose.yml.
        _healthy = new ContainerBuilder()
            .WithImage(Tag)
            .WithPortBinding(ContainerPort, true)
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.Tmpfs = new Dictionary<string, string> { ["/tmp"] = "size=16m,mode=1777" };
                p.HostConfig.ReadonlyRootfs = true;
                p.HostConfig.CapDrop = ["ALL"];
                p.HostConfig.SecurityOpt = ["no-new-privileges:true"];
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ContainerPort).ForPath("/health")))
            .Build();

        _scratch = new ContainerBuilder()
            .WithImage(Tag)
            .WithPortBinding(ContainerPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ContainerPort).ForPath("/health")))
            .Build();

        await Task.WhenAll(_healthy.StartAsync(), _scratch.StartAsync());

        Healthy = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HealthyPort}/") };
    }

    public async Task DisposeAsync()
    {
        Healthy?.Dispose();
        if (_healthy != null) await _healthy.DisposeAsync();
        if (_scratch != null) await _scratch.DisposeAsync();
        await Cli.Wrap("docker").WithArguments(["image", "rm", "-f", Tag]).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
        Directory.Delete(_workDir, recursive: true);
    }

    public static string PackagingDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "packaging", "apps");
                if (Directory.Exists(candidate)) return candidate;
            }

            // The build output may live outside the repository (dotnet --artifacts-path).
            var fromEnvironment = Environment.GetEnvironmentVariable("APPS_TEST_PACKAGING_DIR");
            if (fromEnvironment != null) return fromEnvironment;
            throw new DirectoryNotFoundException("packaging/apps not found above the test output; set APPS_TEST_PACKAGING_DIR");
        }
    }

    public static string Bash =>
        Environment.GetEnvironmentVariable("APPS_TEST_BASH")
        ?? (OperatingSystem.IsWindows()
            // Git Bash, wherever it is installed. A plain "bash" on Windows can resolve to WSL.
            ? Environment.GetLogicalDrives().Select(d => Path.Combine(d, @"Program Files\Git\bin\bash.exe")).First(File.Exists)
            : "bash");

    /// <summary>Runs publish-apps.sh. Paths go through the script's working directory so that Git Bash can resolve them.</summary>
    public static async Task<BufferedCommandResult> Publish(params string[] args) =>
        await PublishCommand(args).ExecuteBufferedAsync();

    /// <summary>publish-apps.sh, for a caller that sets the environment or checks the exit code itself.</summary>
    public static Command PublishCommand(params string[] args) => Cli.Wrap(Bash)
        .WithWorkingDirectory(PackagingDirectory)
        .WithArguments(new[] { "./publish-apps.sh" }.Concat(args.Select(a => File.Exists(a) ? ForBash(a) : a)));

    public static string ForBash(string path) => path.Replace('\\', '/');

    /// <summary>docker exec as root, which the image's own user cannot do; only for the writable scratch container.</summary>
    public async Task ExecAsRoot(string script)
    {
        await Cli.Wrap("docker").WithArguments(["exec", "-u", "0", _scratch.Id, "sh", "-c", script]).ExecuteBufferedAsync();
    }
}

/// <summary>Runs only on Linux, where Docker Engine can publish a port on the bridge gateway address.</summary>
public sealed class LinuxDockerEngineFactAttribute : FactAttribute
{
    public LinuxDockerEngineFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Needs Docker Engine on Linux (Docker Desktop cannot bind a port to 172.17.0.1); runs in CI.";
    }
}

public sealed class AppsImageIntegrationTests(AppsImageFixture image) : IClassFixture<AppsImageFixture>
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>One raw request, because HttpClient would normalise a traversal away before it reached the server.</summary>
    private Task<string> RawGet(string path) => RawRequest(image.HealthyPort, "GET", path);

    private static async Task<string> RawRequest(int port, string method, string path, string extraHeaders = "")
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{method} {path} HTTP/1.1\r\nHost: localhost\r\n{extraHeaders}Connection: close\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.Latin1);
        return await reader.ReadToEndAsync();
    }

    private static int StatusOf(string rawResponse) => int.Parse(rawResponse.Split(' ')[1]);

    private AppsCatalogClient Client() => new(new HttpClient(), new AppsOptions(new Uri($"http://127.0.0.1:{image.HealthyPort}/")));

    [Fact]
    public async Task Manifest_ListsEveryAppAndDownloadInTheGivenOrder()
    {
        var catalog = await Client().GetAsync();

        catalog.Reachable.Should().BeTrue();
        catalog.Apps.Select(a => a.Id).Should().Equal("example-viewer", "example-web");
        var viewer = catalog.Apps[0];
        viewer.Name.Should().Be("Example Viewer");
        viewer.Icon.Should().Be("example-viewer.png");
        viewer.Downloads.Should().HaveCount(3);
        viewer.Downloads[0].Should().Be(new AppDownload(AppPlatform.Windows, AppArchitecture.X64, null, false,
            new AppFile("1.0.0", AppsImageFixture.WindowsFile, image.WindowsBytes.Length), "windows.md"));
        viewer.Downloads[1].Label.Should().Be("Kiosk devices");
        viewer.Downloads[2].Preview.Should().BeTrue();
        catalog.Apps[1].Downloads.Should().Equal(new AppDownload(AppPlatform.Ios, AppArchitecture.Any, null, false, null, null));
    }

    [Fact]
    public async Task Icon_IsServedAsPng_ExactlyAsPublished_AndPassesTheAutoUpdatersCheck()
    {
        using var response = await image.Healthy.GetAsync("icons/example-viewer.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(await File.ReadAllBytesAsync(AppsImageFixture.Icon));
        (await Client().GetIconAsync("example-viewer.png")).Should().NotBeNull();
    }

    [Theory]
    [InlineData("/icons/")]
    [InlineData("/icons/missing.png")]
    [InlineData("/icons/example-viewer.svg")]
    [InlineData("/icons/a%2fexample-viewer.png")]
    public async Task Icon_OnlyAPlainPngName_IsServed(string path)
    {
        StatusOf(await RawGet(path)).Should().BeOneOf(400, 404);
    }

    [Fact]
    public async Task Download_ReturnsTheExactBytes()
    {
        var file = (await Client().GetAsync()).Files.Single(f => f.FileName == AppsImageFixture.WindowsFile);

        var bytes = await image.Healthy.GetByteArrayAsync($"files/{file.FileName}");

        Hash(bytes).Should().Be(Hash(image.WindowsBytes));
        file.Size.Should().Be(image.WindowsBytes.Length);
    }

    [Theory]
    [InlineData("windows.md")]
    [InlineData("android.md")]
    public async Task Guide_IsServedAsUtf8Markdown_ExactlyAsPublished(string name)
    {
        using var response = await image.Healthy.GetAsync($"guides/{name}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        response.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(await File.ReadAllBytesAsync(AppsImageFixture.Guide(name)));
    }

    [Fact]
    public async Task Guide_ThroughTheClient_IsTheMarkdown()
    {
        (await Client().GetGuideAsync("linux.md")).Should().Be(await File.ReadAllTextAsync(AppsImageFixture.Guide("linux.md")));
    }

    [Theory]
    [InlineData("/guides/")]
    [InlineData("/guides/missing.md")]
    [InlineData("/guides/windows.txt")]
    [InlineData("/guides/.md")]
    [InlineData("/guides/a%2fwindows.md")]
    [InlineData("/windows.md")]
    public async Task Guide_OnlyAListedPlainMdName_IsServed(string path)
    {
        var response = await RawGet(path);

        StatusOf(response).Should().BeOneOf(400, 404);
        response.Should().NotContain("schemaVersion");
    }

    [Theory]
    [InlineData(AppsImageFixture.WindowsFile, "application/x-msi")]
    [InlineData(AppsImageFixture.AndroidFile, "application/vnd.android.package-archive")]
    [InlineData(AppsImageFixture.LinuxFile, "application/vnd.debian.binary-package")]
    public async Task Download_IsAnAttachmentUnderItsOwnName(string file, string contentType)
    {
        using var response = await image.Healthy.GetAsync($"files/{file}", HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName!.Trim('"').Should().Be(file);
        response.Content.Headers.ContentType!.MediaType.Should().Be(contentType);
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
    }

    [Fact]
    public async Task RangeRequest_ReturnsOnlyThatRange()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"files/{AppsImageFixture.WindowsFile}");
        request.Headers.Range = new RangeHeaderValue(1000, 1999);

        using var response = await image.Healthy.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.Length.Should().Be(image.WindowsBytes.Length);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(image.WindowsBytes.AsSpan(1000, 1000).ToArray());
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
    }

    [Fact]
    public async Task InterruptedDownload_ResumedWithARange_GivesTheWholeFile()
    {
        // The link drops part way through.
        var received = new MemoryStream();
        using (var first = await image.Healthy.GetAsync($"files/{AppsImageFixture.WindowsFile}", HttpCompletionOption.ResponseHeadersRead))
        await using (var stream = await first.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[256 * 1024];
            await stream.ReadExactlyAsync(buffer);
            received.Write(buffer);
        }

        using var resume = new HttpRequestMessage(HttpMethod.Get, $"files/{AppsImageFixture.WindowsFile}");
        resume.Headers.Range = new RangeHeaderValue(received.Length, null);
        using var rest = await image.Healthy.SendAsync(resume);
        rest.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        await rest.Content.CopyToAsync(received);

        Hash(received.ToArray()).Should().Be(Hash(image.WindowsBytes));
    }

    [Theory]
    [InlineData("/files/../../../../etc/passwd")]
    [InlineData("/files/..%2f..%2f..%2f..%2fetc%2fpasswd")]
    [InlineData("/files/%2e%2e/%2e%2e/%2e%2e/%2e%2e/etc/passwd")]
    [InlineData("/files/....//....//....//etc/passwd")]
    [InlineData("/files/..%5c..%5c..%5cetc%5cpasswd")]
    [InlineData("/../../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("//etc/passwd")]
    [InlineData("/guides/..%2f..%2f..%2fetc%2fpasswd")]
    public async Task Traversal_NeverReadsOutsideTheServedDirectories(string path)
    {
        var response = await RawGet(path);

        StatusOf(response).Should().BeOneOf(400, 403, 404);
        response.Should().NotContain("root:");
    }

    [Theory]
    [InlineData("/files/")]
    [InlineData("/files")]
    [InlineData("/")]
    [InlineData("/SHA256SUMS")]
    [InlineData("/verification.json")]
    public async Task EverythingElse_Is404_WithNoDirectoryListing(string path)
    {
        StatusOf(await RawGet(path)).Should().Be(404);
    }

    private static long DerivedMtime(string sha256) => 946684800 + Convert.ToInt64(sha256[..7], 16);

    [Fact]
    public async Task Container_RunsAsANonRootUser()
    {
        // The uid of the process the container started (nginx), not just of an exec.
        var uid = await image.HealthyContainer.ExecAsync(["awk", "/^Uid:/ { print $2 }", "/proc/1/status"]);

        uid.Stdout.Trim().Should().NotBeEmpty().And.NotBe("0");
    }

    [Fact]
    public async Task Download_SaysNoCache_AndItsValidatorsComeFromTheFileHash()
    {
        var mtime = DerivedMtime(Hash(image.WindowsBytes));

        using var response = await image.Healthy.GetAsync($"files/{AppsImageFixture.WindowsFile}", HttpCompletionOption.ResponseHeadersRead);

        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        response.Content.Headers.LastModified.Should().Be(DateTimeOffset.FromUnixTimeSeconds(mtime));
        // The latest possible derived date (base + 16^7 s) is still in the past: RFC 9110 forbids a future Last-Modified.
        DateTimeOffset.FromUnixTimeSeconds(946684800 + 0xFFFFFFF).Should().BeBefore(response.Headers.Date!.Value);
        response.Headers.ETag!.Tag.Should().Be($"\"{mtime:x}-{image.WindowsBytes.Length:x}\"");
    }

    [Fact]
    public async Task ResumeWithTheValidatorOfAnotherBuild_GetsTheWholeFile_NeverAMix()
    {
        var size = image.WindowsBytes.Length;
        using var current = new HttpRequestMessage(HttpMethod.Get, $"files/{AppsImageFixture.WindowsFile}");
        current.Headers.Range = new RangeHeaderValue(1000, 1999);
        current.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{DerivedMtime(Hash(image.WindowsBytes)):x}-{size:x}\""));
        using var older = new HttpRequestMessage(HttpMethod.Get, $"files/{AppsImageFixture.WindowsFile}");
        older.Headers.Range = new RangeHeaderValue(1000, 1999);
        older.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"5e0be100-{size:x}\""));

        using var sameBuild = await image.Healthy.SendAsync(current);
        using var otherBuild = await image.Healthy.SendAsync(older);

        sameBuild.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        otherBuild.StatusCode.Should().Be(HttpStatusCode.OK);
        Hash(await otherBuild.Content.ReadAsByteArrayAsync()).Should().Be(Hash(image.WindowsBytes));
    }

    [Fact]
    public async Task MultipleRanges_AreIgnored_AndTheWholeFileIsSent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"files/{AppsImageFixture.WindowsFile}");
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-1,5-6");

        using var response = await image.Healthy.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Hash(await response.Content.ReadAsByteArrayAsync()).Should().Be(Hash(image.WindowsBytes));
    }

    [Fact]
    public async Task PercentEncodedName_ReachesTheSameFile()
    {
        // %45 is "E": nginx decodes before matching, so the encoded form is the same file under the same rules.
        var response = await RawGet($"/files/%45{AppsImageFixture.WindowsFile[1..]}");

        StatusOf(response).Should().Be(200);
    }

    [Fact]
    public async Task Symlink_IsRefused()
    {
        await image.ExecAsRoot("ln -sf /etc/passwd /srv/apps/files/link.bin && ln -sf /etc/passwd /srv/apps/guides/link.md");

        var file = await RawRequest(image.ScratchPort, "GET", "/files/link.bin");
        var guide = await RawRequest(image.ScratchPort, "GET", "/guides/link.md");

        StatusOf(file).Should().Be(403);
        StatusOf(guide).Should().Be(403);
        file.Should().NotContain("root:");
        guide.Should().NotContain("root:");
    }

    [Fact]
    public async Task DotFile_Is404_EvenWhenItExists()
    {
        await image.ExecAsRoot("printf secret > /srv/apps/files/.hidden");

        var response = await RawRequest(image.ScratchPort, "GET", "/files/.hidden");

        StatusOf(response).Should().Be(404);
        response.Should().NotContain("secret");
    }

    [Fact]
    public async Task UnchangedFiles_KeepTheirLayers_WhenAnotherAppChanges()
    {
        var dir = Directory.CreateTempSubdirectory("apps-layers-");
        var tag = $"apps-test:{Guid.NewGuid():N}";
        try
        {
            // The first file changes, so every layer after it is rebuilt, by the builder publish-apps.sh always uses (the same
            // one as for a push): each must still come out byte-identical.
            var windows = Path.Combine(dir.FullName, AppsImageFixture.WindowsFile);
            await File.WriteAllBytesAsync(windows, RandomNumberGenerator.GetBytes(64 * 1024));
            await AppsImageFixture.Publish(["--release", "1.0.1", "--local-tag", tag,
                .. AppsImageFixture.AppArguments(windows, image.LinuxPath, image.AndroidPath)]);

            var first = await Layers(image.Tag);
            var second = await Layers(tag);

            // Layer order: base, windows, linux, android, their three guides, the icon, then manifest.json.
            second.Should().HaveCount(first.Length);
            second[..^8].Should().Equal(first[..^8], "the base did not change");
            second[^8].Should().NotBe(first[^8], "the windows file changed");
            second[^7..^1].Should().Equal(first[^7..^1], "linux, android, the guides and the icon did not change");
            second[^1].Should().NotBe(first[^1], "the manifest changed");
        }
        finally
        {
            await Cli.Wrap("docker").WithArguments(["image", "rm", "-f", tag]).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
            dir.Delete(recursive: true);
        }

        static async Task<string[]> Layers(string tag)
        {
            var inspect = await Cli.Wrap("docker").WithArguments(["image", "inspect", "-f", "{{range .RootFS.Layers}}{{println .}}{{end}}", tag]).ExecuteBufferedAsync();
            return inspect.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    /// <summary>
    /// publish-apps.sh against a stub docker that answers the tag lookup as told and records whether a push was attempted.
    /// </summary>
    [Theory]
    [InlineData(1, "ERROR: docker.example.test/example/apps:9.9.9: not found", true, "")]
    [InlineData(1, "ERROR: failed to authorize: failed to fetch oauth token: 401 Unauthorized", false, "could not check")]
    [InlineData(1, "ERROR: unauthorized: authentication required (repository not found)", false, "could not check")]
    [InlineData(1, "ERROR: dial tcp: lookup docker.example.test: no such host", false, "could not check")]
    [InlineData(0, "Name: docker.example.test/example/apps:9.9.9", false, "already exists")]
    public async Task PublishScript_PushesOnlyWhenTheRegistrySaysTheTagIsNotFound(int inspectExit, string inspectOutput, bool pushes, string refusal)
    {
        var dir = Directory.CreateTempSubdirectory("apps-publish-stub-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(dir.FullName, "bin")).FullName;
            var log = Path.Combine(dir.FullName, "docker.log");
            await File.WriteAllTextAsync(Path.Combine(bin, "docker"), """
                #!/bin/sh
                echo "$*" >> "$STUB_LOG"
                if [ "$1 $2 $3" = "buildx imagetools inspect" ]; then echo "$STUB_INSPECT_OUTPUT" >&2; exit "$STUB_INSPECT_EXIT"; fi
                exit 0
                """.Replace("\r", ""));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(bin, "docker"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var app = Path.Combine(dir.FullName, "a.exe");
            await File.WriteAllBytesAsync(app, [1, 2, 3]);

            var run = await AppsImageFixture.PublishCommand("--release", "9.9.9", "--image", "docker.example.test/example/apps",
                    "--app", "viewer", "--name", "Example Viewer", "--download", "--platform", "windows", "--arch", "x64", "--file", app, "--version", "1.0.0")
                .WithEnvironmentVariables(e => e
                    .Set("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"))
                    .Set("STUB_LOG", AppsImageFixture.ForBash(log))
                    .Set("STUB_INSPECT_EXIT", inspectExit.ToString())
                    .Set("STUB_INSPECT_OUTPUT", inspectOutput))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            var calls = await File.ReadAllTextAsync(log);
            calls.Should().Contain("buildx imagetools inspect docker.example.test/example/apps:9.9.9", "the stub docker must be the one called");
            if (pushes)
            {
                run.ExitCode.Should().Be(0, run.StandardError);
                calls.Should().Contain("buildx build --builder apps-publish --platform linux/amd64,linux/arm64 " +
                    "--output type=image,name=docker.example.test/example/apps:9.9.9,push=true,rewrite-timestamp=true");
            }
            else
            {
                run.ExitCode.Should().NotBe(0);
                run.StandardError.Should().Contain(refusal);
                calls.Should().NotContain("buildx build");
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>A dry run validates and prints the manifest; the AutoUpdater reads back exactly what was given.</summary>
    [Fact]
    public async Task PublishScript_DryRunManifest_IsWhatTheAutoUpdaterReads()
    {
        var run = await AppsImageFixture.Publish(["--release", "2.0.0", "--dry-run",
            .. AppsImageFixture.AppArguments(image.WindowsPath, image.LinuxPath, image.AndroidPath),
            "--app", "quoted", "--name", "Viewer \"quoted\" \\ edition",
            "--download", "--platform", "macos", "--arch", "any", "--label", "Label \"q\" \\ x"]);

        var catalog = AppsManifest.Parse(run.StandardOutput);

        catalog.Reachable.Should().BeTrue(run.StandardOutput);
        catalog.Apps.Select(a => a.Id).Should().Equal("example-viewer", "example-web", "quoted");
        catalog.Apps[2].Name.Should().Be("Viewer \"quoted\" \\ edition");
        catalog.Apps[2].Downloads.Single().Label.Should().Be("Label \"q\" \\ x");
        catalog.Apps[0].Downloads.Select(d => d.Guide).Should().Equal("windows.md", "linux.md", "android.md");
        catalog.Apps[0].Icon.Should().Be("example-viewer.png");
    }

    public static TheoryData<string[], string> RefusedPublishes()
    {
        var file = Path.Combine(Path.GetTempPath(), "apps-refuse-a.exe");
        File.WriteAllBytes(file, [1, 2, 3]);
        var svg = Path.Combine(Path.GetTempPath(), "apps-refuse-icon.png");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>");
        var huge = Path.Combine(Path.GetTempPath(), "apps-refuse-huge.png");
        File.WriteAllBytes(huge, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[256 * 1024]]);
        string[] download = ["--download", "--platform", "windows", "--arch", "x64"];
        return new TheoryData<string[], string>
        {
            { ["--app", "viewer", "--name", new string('x', 81)], "longer than 80" },
            { ["--app", "viewer", "--name", "tab\there"], "control or layout character" },
            { ["--app", "viewer", "--name", "   "], "blank" },
            { ["--app", "Viewer", "--name", "x"], "id not allowed" },
            { ["--app", "viewer", "--name", "x", "--app", "viewer", "--name", "y"], "given twice" },
            { ["--name", "x"], "must follow an --app" },
            { ["--app", "viewer", "--name", "x", "--platform", "windows"], "must follow a --download" },
            { ["--app", "viewer", "--name", "x", "--download", "--platform", "beos", "--arch", "x64"], "--platform must be one of" },
            { ["--app", "viewer", "--name", "x", "--download", "--platform", "windows", "--arch", "riscv"], "--arch must be one of" },
            { ["--app", "viewer", "--name", "x", "--download", "--arch", "x64"], "--platform must be one of" },
            { ["--app", "viewer", "--name", "x", .. download, "--label", new string('x', 41)], "longer than 40" },
            { ["--app", "viewer", "--name", "x", .. download, .. download], "same platform, architecture and label" },
            { ["--app", "viewer", "--name", "x", .. Enumerable.Range(0, 9).SelectMany(i => download.Append("--label").Append($"v{i}"))], "at most 8 downloads" },
            { ["--app", "viewer", "--name", "x", .. download, "--version", "1.0.0"], "--version given without --file" },
            { ["--app", "viewer", "--name", "x", .. download, "--file", file], "--version is missing" },
            { ["--app", "viewer", "--name", "x", .. download, "--guide", file], "guide name not allowed" },
            { ["--app", "viewer", "--name", "x", "--icon", file], "icon name not allowed" },
            { ["--app", "viewer", "--name", "x", "--icon", svg], "icon is not a PNG" },
            { ["--app", "viewer", "--name", "x", "--icon", huge], "icon must be 1 to 262144 bytes" },
            { [.. Enumerable.Range(0, 17).SelectMany(i => new[] { "--app", $"app-{i}", "--name", "x" })], "at most 16 apps" },
        };
    }

    [Theory]
    [MemberData(nameof(RefusedPublishes))]
    public async Task PublishScript_RefusesWhatThePageWouldDrop(string[] apps, string error)
    {
        var run = await AppsImageFixture.PublishCommand(["--release", "1.0.0", "--dry-run", .. apps])
            .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

        run.ExitCode.Should().NotBe(0);
        run.StandardError.Should().Contain(error);
    }

    [Theory]
    [InlineData("invalid-utf8", "not valid UTF-8")]
    [InlineData("too-large", "must be 1 to 16384 bytes")]
    [InlineData("empty", "must be 1 to 16384 bytes")]
    public async Task PublishScript_RefusesAGuideTheAutoUpdaterWouldNotRead(string kind, string error)
    {
        var dir = Directory.CreateTempSubdirectory("apps-guide-");
        try
        {
            var guide = Path.Combine(dir.FullName, "viewer.md");
            await File.WriteAllBytesAsync(guide, kind switch
            {
                "invalid-utf8" => [0x23, 0x20, 0xFF, 0xFE],
                "too-large" => Enumerable.Repeat((byte)'a', 16385).ToArray(),
                _ => [],
            });

            var run = await AppsImageFixture.PublishCommand("--release", "1.0.0", "--dry-run", "--app", "viewer", "--name", "x", "--download", "--platform", "windows", "--arch", "x64", "--guide", guide)
                .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

            run.ExitCode.Should().NotBe(0);
            run.StandardError.Should().Contain(error);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PublishScript_PushWithoutAnImage_IsRefused()
    {
        var run = await AppsImageFixture.PublishCommand("--release", "1.0.0", "--app", "viewer", "--name", "x")
            .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

        run.ExitCode.Should().NotBe(0);
        run.StandardError.Should().Contain("--image REGISTRY/REPOSITORY is required");
    }

    [Fact]
    public async Task PublishScript_RefusesAFileNameThatIsNotAPlainName()
    {
        var dir = Directory.CreateTempSubdirectory("apps-publish-");
        try
        {
            var bad = Path.Combine(dir.FullName, "has space.exe");
            await File.WriteAllBytesAsync(bad, [1, 2, 3]);

            var publish = () => AppsImageFixture.Publish("--release", "1.0.0", "--dry-run", "--app", "viewer", "--name", "x", "--download", "--platform", "windows", "--arch", "x64", "--file", bad, "--version", "1.0.0");

            await publish.Should().ThrowAsync<Exception>().Where(e => e.Message.Contains("file name not allowed"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// release.sh in a throwaway clone of a throwaway bare origin. Each case must stop before writing anything.
    /// </summary>
    [Theory]
    [InlineData("remote", "already exists on origin")]
    [InlineData("local", "already exists locally")]
    [InlineData("unreachable", "Could not ask origin")]
    public async Task ReleaseScript_ExistingOrUnknownTag_StopsBeforeChangingTheTree(string situation, string expected)
    {
        var dir = Directory.CreateTempSubdirectory("apps-release-");
        try
        {
            var setup = $$"""
                set -e
                git init -q --bare origin.git
                git init -q -b master repo
                cp -r "{{AppsImageFixture.ForBash(Path.Combine(AppsImageFixture.PackagingDirectory, "compose"))}}"/. repo/
                cd repo
                git config user.name test && git config user.email test@example.invalid && git config core.autocrlf false
                git remote add origin ../origin.git
                git add -A && git commit -qm init && git push -q origin master
                case "{{situation}}" in
                    remote) git tag v1.0.0 && git push -q origin v1.0.0 && git tag -d v1.0.0 > /dev/null ;;
                    local) git tag v1.0.0 ;;
                    unreachable) git remote set-url origin ../missing.git ;;
                esac
                """;
            await Cli.Wrap(AppsImageFixture.Bash).WithWorkingDirectory(dir.FullName).WithArguments(["-c", setup.Replace("\r", "")]).ExecuteBufferedAsync();
            var repo = Path.Combine(dir.FullName, "repo");

            var release = await Cli.Wrap(AppsImageFixture.Bash).WithWorkingDirectory(repo).WithArguments(["./release.sh", "1.0.0"])
                .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
            var status = await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(["status", "--porcelain"]).ExecuteBufferedAsync();

            release.ExitCode.Should().NotBe(0);
            release.StandardError.Should().Contain(expected);
            status.StandardOutput.Should().BeEmpty("nothing may be pinned or written when the release stops");
            File.Exists(Path.Combine(repo, "up-1.0.0.sh")).Should().BeFalse();
        }
        finally
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            dir.Delete(recursive: true);
        }
    }

    /// <summary>update-version.sh list with curl and jq replaced by exported shell functions, which win over any binary on
    /// PATH (Git Bash puts its own curl first). Returns the exit code, all output, the curl config and URL piped in, if any.</summary>
    private static async Task<(int ExitCode, string Output, string? CurlConfig)> ListVersions(
        int curlExit, string curlStderr, string? user = null, string? password = null)
    {
        var dir = Directory.CreateTempSubdirectory("apps-list-");
        try
        {
            var config = Path.Combine(dir.FullName, "curl-config");
            const string script = """
                curl() {
                    for a in "$@"; do if [ "$a" = "-K" ]; then cat > "$STUB_CFG"; fi; done
                    if [ "$STUB_CURL_EXIT" = 0 ]; then echo '[{"tags":[{"name":"1.2.0"}]}]'; fi
                    if [ -n "${STUB_CURL_ERR:-}" ]; then echo "$STUB_CURL_ERR" >&2; fi
                    return "$STUB_CURL_EXIT"
                }
                jq() {
                    local in; in=$(cat)
                    case "$in" in *tags*) printf '1.2.0\nlatest\n' ;; esac
                    return 0
                }
                export -f curl jq
                exec ./update-version.sh list
                """;

            var run = await Cli.Wrap(AppsImageFixture.Bash)
                .WithWorkingDirectory(Path.Combine(AppsImageFixture.PackagingDirectory, "compose"))
                .WithArguments(["-c", script.Replace("\r", "")])
                .WithEnvironmentVariables(e =>
                {
                    e.Set("STUB_CFG", AppsImageFixture.ForBash(config))
                        .Set("STUB_CURL_EXIT", curlExit.ToString())
                        .Set("STUB_CURL_ERR", curlStderr);
                    if (user != null) e.Set("HARBOR_USERNAME", user).Set("HARBOR_PASSWORD", password);
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            var piped = File.Exists(config) ? await File.ReadAllTextAsync(config) : null;
            return (run.ExitCode, run.StandardOutput + run.StandardError, piped);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(22, "curl: (22) The requested URL returned error: 401")]
    [InlineData(6, "curl: (6) Could not resolve host: registry.example.com")]
    [InlineData(7, "curl: (7) Failed to connect to registry.example.com port 443: Connection refused")]
    public async Task UpdateVersionList_RegistryRefusesOrIsUnreachable_SaysSo(int curlExit, string curlError)
    {
        var (exitCode, output, _) = await ListVersions(curlExit, curlError);

        exitCode.Should().NotBe(0);
        output.Should().Contain("Cannot reach the Harbor registry API at https://registry.example.com/api/v2.0")
            .And.NotContain("No release tags found");
    }

    [Fact]
    public async Task UpdateVersionList_RegistryAnswers_ListsTheReleaseTags()
    {
        var (exitCode, output, _) = await ListVersions(0, "");

        exitCode.Should().Be(0, output);
        output.Should().Contain("1.2.0");
    }

    [Fact]
    public async Task UpdateVersionList_CredentialsWithQuoteAndBackslash_ReachCurlEscaped()
    {
        var (exitCode, output, config) = await ListVersions(0, "", user: "robot$example+pull", password: "a\"b\\c");

        exitCode.Should().Be(0, output);
        config.Should().Be("user = \"robot$example+pull:a\\\"b\\\\c\"\n");
        output.Should().NotContain("a\"b\\c");
    }

    /// <summary>
    /// The compose package as a station runs it: the port is bound to the Docker bridge's gateway, reachable from a container
    /// on another bridge network (where the AutoUpdater runs), and nothing listens on the LAN address. It proves the bind, not
    /// the routed case: a LAN host that routes 172.17.0.0/16 to the station could still reach the port (README: tidying, not a
    /// security boundary). Needs Docker Engine on Linux: Docker Desktop cannot bind a host port to that address.
    /// </summary>
    [LinuxDockerEngineFact]
    public async Task ComposePort_IsOnTheBridgeGateway_ReachableFromAContainer_NotFromTheLan()
    {
        var project = $"apps-bind-{Guid.NewGuid():N}"[..20];
        var port = 18000 + Random.Shared.Next(1000);
        var dir = Directory.CreateTempSubdirectory("apps-compose-");
        var compose = Path.Combine(AppsImageFixture.PackagingDirectory, "compose", "docker-compose.yml");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "override.yml"),
            $"services:\n  apps:\n    image: {image.Tag}\n    container_name: {project}\n");
        Command Compose(params string[] args) => Cli.Wrap("docker")
            .WithArguments(new[] { "compose", "-p", project, "-f", compose, "-f", Path.Combine(dir.FullName, "override.yml") }.Concat(args))
            .WithEnvironmentVariables(e => e.Set("APPS_PORT", port.ToString()));
        try
        {
            await Compose("up", "-d", "--wait").ExecuteBufferedAsync();
            var published = await Cli.Wrap("docker").WithArguments(["port", project, "8080"]).ExecuteBufferedAsync();
            await Cli.Wrap("docker").WithArguments(["network", "create", project]).ExecuteBufferedAsync();
            var fromContainer = await Cli.Wrap("docker")
                .WithArguments(["run", "--rm", "--network", project, "--entrypoint", "wget", image.Tag, "-qO-", "-T", "5", $"http://172.17.0.1:{port}/health"])
                .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
            var lanAddress = (await Cli.Wrap("hostname").WithArguments(["-I"]).ExecuteBufferedAsync()).StandardOutput
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).First(a => !a.StartsWith("172.17."));
            using var lan = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var fromLan = async () => await lan.GetAsync($"http://{lanAddress}:{port}/health");

            published.StandardOutput.Trim().Should().Be($"172.17.0.1:{port}");
            fromContainer.StandardOutput.Should().Be("ok\n", fromContainer.StandardError);
            await fromLan.Should().ThrowAsync<HttpRequestException>();
        }
        finally
        {
            await Compose("down", "-v").WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
            await Cli.Wrap("docker").WithArguments(["network", "rm", project]).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReleaseScript_MessageOptionWithoutAValue_IsAClearError()
    {
        var release = await Cli.Wrap(AppsImageFixture.Bash)
            .WithWorkingDirectory(Path.Combine(AppsImageFixture.PackagingDirectory, "compose"))
            .WithArguments(["./release.sh", "1.0.0", "-m"])
            .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

        release.ExitCode.Should().NotBe(0);
        release.StandardError.Should().Contain("needs a message");
    }

    private const string PruneStubDocker = """
        #!/bin/sh
        if [ "$1 $2" = "image ls" ]; then echo "$3" > /tmp/prune/listed; printf '1.9.0\n1.10.0\n1.2.0\nlatest\n<none>\n0.1.0\n'; exit 0; fi
        if [ "$1 $2" = "image rm" ]; then echo "$3" >> /tmp/prune/removed; case "$3" in *:1.2.0) exit 1 ;; esac; exit 0; fi
        exit 1
        """;

    private async Task<(long ExitCode, string Listed, string[] Removed)> Prune(string environment, bool withCompose)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppsImageFixture.PackagingDirectory, "compose", "prune-old-images.sh"));
        var compose = await File.ReadAllTextAsync(Path.Combine(AppsImageFixture.PackagingDirectory, "compose", "docker-compose.yml"));

        await image.ScratchContainer.ExecAsync(["sh", "-c", "rm -rf /tmp/prune && mkdir -p /tmp/prune/bin /tmp/prune/pkg"]);
        await image.ScratchContainer.CopyAsync(Encoding.UTF8.GetBytes(PruneStubDocker.Replace("\r", "")), "/tmp/prune/bin/docker", fileMode: (UnixFileModes)Convert.ToInt32("755", 8));
        await image.ScratchContainer.CopyAsync(Encoding.UTF8.GetBytes(script.Replace("\r", "")), "/tmp/prune/pkg/prune-old-images.sh", fileMode: (UnixFileModes)Convert.ToInt32("755", 8));
        if (withCompose)
            await image.ScratchContainer.CopyAsync(Encoding.UTF8.GetBytes(compose.Replace("\r", "")), "/tmp/prune/pkg/docker-compose.yml", fileMode: (UnixFileModes)Convert.ToInt32("644", 8));

        var run = await image.ScratchContainer.ExecAsync(["sh", "-c", $"PATH=/tmp/prune/bin:$PATH {environment} sh /tmp/prune/pkg/prune-old-images.sh"]);
        var listed = await image.ScratchContainer.ExecAsync(["sh", "-c", "cat /tmp/prune/listed 2>/dev/null"]);
        var removed = await image.ScratchContainer.ExecAsync(["sh", "-c", "cat /tmp/prune/removed 2>/dev/null"]);
        return (run.ExitCode, listed.Stdout.Trim(), removed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Prune_KeepsTheTwoNewestByVersion_AndAlwaysExitsZero()
    {
        var (exitCode, _, removed) = await Prune("APPS_IMAGE_REPOSITORY=repo", withCompose: false);

        exitCode.Should().Be(0, "removing 1.2.0 failed in the stub, and that must not fail the update");
        removed.Should().Equal("repo:1.2.0", "repo:0.1.0");
    }

    [Fact]
    public async Task Prune_ReadsTheRepositoryFromThePackagesComposeFile()
    {
        var (exitCode, listed, removed) = await Prune("", withCompose: true);

        exitCode.Should().Be(0);
        listed.Should().Be("registry.example.com/example/apps");
        removed.Should().Equal("registry.example.com/example/apps:1.2.0", "registry.example.com/example/apps:0.1.0");
    }

    [Fact]
    public async Task Prune_NoComposeFileAndNoOverride_RemovesNothing_AndExitsZero()
    {
        var (exitCode, listed, removed) = await Prune("", withCompose: false);

        exitCode.Should().Be(0);
        listed.Should().BeEmpty();
        removed.Should().BeEmpty();
    }
}
