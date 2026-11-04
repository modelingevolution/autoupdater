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
/// Builds the apps image through the real publish script, with dummy files, and runs it the way the compose package does.
/// </summary>
public sealed class AppsImageFixture : IAsyncLifetime
{
    private const int ContainerPort = 8080;
    public const string WindowsFile = "RocketWelderPlayer-1.0.0.msi";
    public const string AndroidFile = "RocketWelderPlayer-1.0.0-arm64-v8a.apk";
    public const string LinuxFile = "native-player_1.0.0_arm64.deb";

    private readonly string _workDir = Directory.CreateTempSubdirectory("apps-image-").FullName;
    private IContainer _healthy = null!;
    private IContainer _tampered = null!;
    private IContainer _noCheck = null!;

    public string Tag { get; } = $"rocketwelder-apps-test:{Guid.NewGuid():N}";
    public byte[] WindowsBytes { get; } = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
    public HttpClient Healthy { get; private set; } = null!;
    public HttpClient Tampered { get; private set; } = null!;
    /// <summary>The image started without its start-up check, as another entrypoint would.</summary>
    public HttpClient NoCheck { get; private set; } = null!;
    public IContainer HealthyContainer => _healthy;
    public string WindowsPath => Path.Combine(_workDir, WindowsFile);
    public string LinuxPath => Path.Combine(_workDir, LinuxFile);
    /// <summary>Its root filesystem is writable and its /tmp allows exec, unlike the compose-restricted one.</summary>
    public IContainer ScratchContainer => _tampered;
    public int HealthyPort => _healthy.GetMappedPublicPort(ContainerPort);
    public int TamperedPort => _tampered.GetMappedPublicPort(ContainerPort);

    public async Task InitializeAsync()
    {
        var windows = Path.Combine(_workDir, WindowsFile);
        var android = Path.Combine(_workDir, AndroidFile);
        var linux = Path.Combine(_workDir, LinuxFile);
        await File.WriteAllBytesAsync(windows, WindowsBytes);
        await File.WriteAllBytesAsync(android, RandomNumberGenerator.GetBytes(64 * 1024));
        await File.WriteAllBytesAsync(linux, RandomNumberGenerator.GetBytes(64 * 1024));

        await Publish(
            "--release", "1.0.0", "--local-tag", Tag,
            "--windows", windows, "--windows-version", "1.0.0",
            "--android", android, "--android-version", "1.0.0",
            "--linux", linux, "--linux-version", "1.0.0");

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

        // The files on disk no longer match the manifest: one is altered, one nobody listed. Copied before it starts.
        _tampered = new ContainerBuilder()
            .WithImage(Tag)
            .WithPortBinding(ContainerPort, true)
            .WithResourceMapping(RandomNumberGenerator.GetBytes(1024), $"/srv/apps/files/{AndroidFile}")
            .WithResourceMapping(Encoding.UTF8.GetBytes("nobody listed me"), "/srv/apps/files/unlisted.bin")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ContainerPort).ForPath("/health")))
            .Build();

        _noCheck = new ContainerBuilder()
            .WithImage(Tag)
            .WithPortBinding(ContainerPort, true)
            .WithEntrypoint("nginx", "-e", "/dev/stderr", "-g", "daemon off;")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ContainerPort).ForPath("/health")))
            .Build();

        await Task.WhenAll(_healthy.StartAsync(), _tampered.StartAsync(), _noCheck.StartAsync());

        Healthy = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HealthyPort}/") };
        Tampered = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{TamperedPort}/") };
        NoCheck = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_noCheck.GetMappedPublicPort(ContainerPort)}/") };
    }

    public async Task DisposeAsync()
    {
        Healthy?.Dispose();
        Tampered?.Dispose();
        NoCheck?.Dispose();
        if (_healthy != null) await _healthy.DisposeAsync();
        if (_tampered != null) await _tampered.DisposeAsync();
        if (_noCheck != null) await _noCheck.DisposeAsync();
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
        .WithArguments(new[] { "./publish-apps.sh" }.Concat(args.Select(ForBash)));

    public static string ForBash(string path) => path.Replace('\\', '/');

    /// <summary>docker exec as root, which the image's own user cannot do; only for the writable scratch container.</summary>
    public async Task ExecAsRoot(string script)
    {
        await Cli.Wrap("docker").WithArguments(["exec", "-u", "0", _tampered.Id, "sh", "-c", script]).ExecuteBufferedAsync();
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

    [Fact]
    public async Task Download_ReturnsTheExactBytes_MatchingTheManifestHash()
    {
        var catalog = await new AppsCatalogClient(new HttpClient(), new AppsOptions("rocketwelder-apps", image.HealthyPort, new Uri($"http://127.0.0.1:{image.HealthyPort}/"))).GetAsync();
        var file = catalog.Find("windows")!;

        var bytes = await image.Healthy.GetByteArrayAsync($"files/{file.FileName}");

        Hash(bytes).Should().Be(file.Sha256).And.Be(Hash(image.WindowsBytes));
        file.Size.Should().Be(image.WindowsBytes.Length);
        file.Verified.Should().BeTrue();
    }

    [Fact]
    public async Task Manifest_ListsEveryAppThePublishScriptWasGiven()
    {
        var catalog = await new AppsCatalogClient(new HttpClient(), new AppsOptions("rocketwelder-apps", image.HealthyPort, new Uri($"http://127.0.0.1:{image.HealthyPort}/"))).GetAsync();

        catalog.Release.Should().Be("1.0.0");
        catalog.Files.Select(f => f.Platform).Should().BeEquivalentTo("windows", "linux", "android");
        catalog.Files.Should().OnlyContain(f => f.Verified);
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
    public async Task Traversal_NeverReadsOutsideTheFilesDirectory(string path)
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
    public async Task EverythingElse_Is404_WithNoDirectoryListing(string path)
    {
        StatusOf(await RawGet(path)).Should().Be(404);
    }

    [Fact]
    public async Task HashMismatch_FileIsNotServed_AndTheOthersStillAre()
    {
        using var altered = await image.Tampered.GetAsync($"files/{AppsImageFixture.AndroidFile}", HttpCompletionOption.ResponseHeadersRead);
        altered.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var intact = await image.Tampered.GetAsync($"files/{AppsImageFixture.WindowsFile}", HttpCompletionOption.ResponseHeadersRead);
        intact.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnlistedFile_IsNotServed()
    {
        using var response = await image.Tampered.GetAsync("files/unlisted.bin", HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task HashMismatch_ReachesTheCatalog_AsNotVerified()
    {
        var catalog = AppsManifest.Parse(
            await image.Tampered.GetStringAsync("manifest.json"),
            await image.Tampered.GetStringAsync("verification.json"));

        catalog.Find("android")!.Verified.Should().BeFalse();
        catalog.Find("windows")!.Verified.Should().BeTrue();
    }

    private static long DerivedMtime(string sha256) => 946684800 + Convert.ToInt64(sha256[..7], 16);

    private async Task<AppFile> ManifestEntry(string platform) =>
        (await new AppsCatalogClient(new HttpClient(), new AppsOptions("rocketwelder-apps", image.HealthyPort, new Uri($"http://127.0.0.1:{image.HealthyPort}/"))).GetAsync())
            .Find(platform)!;

    [Fact]
    public async Task Container_RunsAsANonRootUser()
    {
        // The uid of the process the container started (verify-files.sh, then nginx in its place), not just of an exec.
        var uid = await image.HealthyContainer.ExecAsync(["awk", "/^Uid:/ { print $2 }", "/proc/1/status"]);

        uid.Stdout.Trim().Should().NotBeEmpty().And.NotBe("0");
    }

    [Fact]
    public async Task Download_SaysNoCache_AndItsValidatorsComeFromTheFileHash()
    {
        var file = await ManifestEntry("windows");
        var mtime = DerivedMtime(file.Sha256);

        using var response = await image.Healthy.GetAsync($"files/{file.FileName}", HttpCompletionOption.ResponseHeadersRead);

        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        response.Content.Headers.LastModified.Should().Be(DateTimeOffset.FromUnixTimeSeconds(mtime));
        // The latest possible derived date (base + 16^7 s) is still in the past: RFC 9110 forbids a future Last-Modified.
        DateTimeOffset.FromUnixTimeSeconds(946684800 + 0xFFFFFFF).Should().BeBefore(response.Headers.Date!.Value);
        response.Headers.ETag!.Tag.Should().Be($"\"{mtime:x}-{file.Size:x}\"");
    }

    [Fact]
    public async Task ResumeWithTheValidatorOfAnotherBuild_GetsTheWholeFile_NeverAMix()
    {
        var file = await ManifestEntry("windows");
        using var current = new HttpRequestMessage(HttpMethod.Get, $"files/{file.FileName}");
        current.Headers.Range = new RangeHeaderValue(1000, 1999);
        current.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{DerivedMtime(file.Sha256):x}-{file.Size:x}\""));
        using var older = new HttpRequestMessage(HttpMethod.Get, $"files/{file.FileName}");
        older.Headers.Range = new RangeHeaderValue(1000, 1999);
        older.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"5e0be100-{file.Size:x}\""));

        using var sameBuild = await image.Healthy.SendAsync(current);
        using var otherBuild = await image.Healthy.SendAsync(older);

        sameBuild.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        otherBuild.StatusCode.Should().Be(HttpStatusCode.OK);
        Hash(await otherBuild.Content.ReadAsByteArrayAsync()).Should().Be(file.Sha256);
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
    public async Task RestartAfterAFileChanged_DoesNotServeItOnAnOldMarker()
    {
        // No tmpfs: /tmp lives in the container's writable layer and survives a restart, as with a plain docker run.
        var container = new ContainerBuilder()
            .WithImage(image.Tag)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health")))
            .Build();
        await using (container)
        {
            await container.StartAsync();
            (await StatusAfterStart()).Should().Be(200, "the file matched at the first start, so it has a marker");

            await Cli.Wrap("docker").WithArguments(["exec", "-u", "0", container.Id, "sh", "-c",
                $"printf changed > /srv/apps/files/{AppsImageFixture.WindowsFile}"]).ExecuteBufferedAsync();
            await container.StopAsync();
            await container.StartAsync();

            (await StatusAfterStart()).Should().Be(503);
        }

        async Task<int> StatusAfterStart() =>
            StatusOf(await RawRequest(container.GetMappedPublicPort(8080), "HEAD", $"/files/{AppsImageFixture.WindowsFile}"));
    }

    [Fact]
    public async Task PercentEncodedName_ReachesTheSameFile()
    {
        // %52 is "R": nginx decodes before matching, so the encoded form is the same file under the same rules.
        var response = await RawGet($"/files/%52{AppsImageFixture.WindowsFile[1..]}");

        StatusOf(response).Should().Be(200);
    }

    [Theory]
    [InlineData("HEAD", "")]
    [InlineData("GET", "Range: bytes=0-99\r\n")]
    [InlineData("GET", "Range: bytes=-1\r\n")]
    public async Task BlockedFile_Is503_ForEveryWayOfAskingForIt(string method, string headers)
    {
        var response = await RawRequest(image.TamperedPort, method, $"/files/{AppsImageFixture.AndroidFile}", headers);

        StatusOf(response).Should().Be(503);
    }

    [Fact]
    public async Task BlockedFile_PercentEncoded_Is503()
    {
        // %52 is "R", the first letter of the Android file's name.
        var response = await RawRequest(image.TamperedPort, "GET", $"/files/%52{AppsImageFixture.AndroidFile[1..]}");

        StatusOf(response).Should().Be(503);
    }

    [Fact]
    public async Task ContainerStartedWithoutItsCheck_ServesNoFile()
    {
        using var response = await image.NoCheck.GetAsync($"files/{AppsImageFixture.WindowsFile}", HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Symlink_IsRefused_EvenWithAnOkMarker()
    {
        // As if the check had passed it: the link exists and carries a marker, so only nginx's own rule stands in the way.
        await image.ExecAsRoot("ln -sf /etc/passwd /srv/apps/files/link.bin && touch /tmp/apps-state/ok/link.bin");

        var response = await RawRequest(image.TamperedPort, "GET", "/files/link.bin");

        StatusOf(response).Should().Be(403);
        response.Should().NotContain("root:");
    }

    [Fact]
    public async Task DotFile_Is404_EvenWhenItExistsWithAnOkMarker()
    {
        await image.ExecAsRoot("printf secret > /srv/apps/files/.hidden && touch /tmp/apps-state/ok/.hidden");

        var response = await RawRequest(image.TamperedPort, "GET", "/files/.hidden");

        StatusOf(response).Should().Be(404);
        response.Should().NotContain("secret");
    }

    [Fact]
    public async Task UnchangedFiles_KeepTheirLayers_WhenAnotherAppChanges()
    {
        var dir = Directory.CreateTempSubdirectory("apps-layers-");
        var tag = $"rocketwelder-apps-test:{Guid.NewGuid():N}";
        try
        {
            var android = Path.Combine(dir.FullName, AppsImageFixture.AndroidFile);
            await File.WriteAllBytesAsync(android, RandomNumberGenerator.GetBytes(64 * 1024));
            await AppsImageFixture.Publish(
                "--release", "1.0.1", "--local-tag", tag,
                "--windows", image.WindowsPath, "--windows-version", "1.0.0",
                "--android", android, "--android-version", "1.0.1",
                "--linux", image.LinuxPath, "--linux-version", "1.0.0");

            var first = await Layers(image.Tag);
            var second = await Layers(tag);

            // Layer order: base, windows, linux, android, then manifest + SHA256SUMS.
            second.Should().HaveCount(first.Length);
            second[..^4].Should().Equal(first[..^4], "the base did not change");
            second[^4].Should().Be(first[^4], "the windows file did not change");
            second[^3].Should().Be(first[^3], "the linux file did not change");
            second[^2].Should().NotBe(first[^2], "the android file changed");
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
    [InlineData(1, "ERROR: docker.example.test/rocketwelder/rocketwelder-apps:9.9.9: not found", true, "")]
    [InlineData(1, "ERROR: failed to authorize: failed to fetch oauth token: 401 Unauthorized", false, "could not check")]
    [InlineData(1, "ERROR: unauthorized: authentication required (repository not found)", false, "could not check")]
    [InlineData(1, "ERROR: dial tcp: lookup docker.example.test: no such host", false, "could not check")]
    [InlineData(0, "Name: docker.example.test/rocketwelder/rocketwelder-apps:9.9.9", false, "already exists")]
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

            var run = await AppsImageFixture.PublishCommand("--release", "9.9.9", "--registry", "docker.example.test", "--windows", app, "--windows-version", "1.0.0")
                .WithEnvironmentVariables(e => e
                    .Set("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"))
                    .Set("STUB_LOG", AppsImageFixture.ForBash(log))
                    .Set("STUB_INSPECT_EXIT", inspectExit.ToString())
                    .Set("STUB_INSPECT_OUTPUT", inspectOutput))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            var calls = await File.ReadAllTextAsync(log);
            calls.Should().Contain("buildx imagetools inspect docker.example.test/rocketwelder/rocketwelder-apps:9.9.9", "the stub docker must be the one called");
            if (pushes)
            {
                run.ExitCode.Should().Be(0, run.StandardError);
                calls.Should().Contain("buildx build --platform");
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
    /// PATH (Git Bash puts its own curl first). Returns the exit code, all output and the curl config piped in, if any.</summary>
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
    [InlineData(6, "curl: (6) Could not resolve host: docker.modelingevolution.com")]
    [InlineData(7, "curl: (7) Failed to connect to docker.modelingevolution.com port 443: Connection refused")]
    public async Task UpdateVersionList_RegistryRefusesOrIsUnreachable_SaysSo(int curlExit, string curlError)
    {
        var (exitCode, output, _) = await ListVersions(curlExit, curlError);

        exitCode.Should().NotBe(0);
        output.Should().Contain("Cannot reach the Harbor registry API").And.NotContain("No release tags found");
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
        var (exitCode, output, config) = await ListVersions(0, "", user: "robot$rocketwelder+pull", password: "a\"b\\c");

        exitCode.Should().Be(0, output);
        config.Should().Be("user = \"robot$rocketwelder+pull:a\\\"b\\\\c\"\n");
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
            $"services:\n  rocketwelder-apps:\n    image: {image.Tag}\n    container_name: {project}\n");
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

    [Fact]
    public async Task Prune_KeepsTheTwoNewestByVersion_AndAlwaysExitsZero()
    {
        // A stub docker that lists tags and records removals. The real script runs inside the image's shell.
        const string stub = """
            #!/bin/sh
            if [ "$1 $2" = "image ls" ]; then printf '1.9.0\n1.10.0\n1.2.0\nlatest\n<none>\n0.1.0\n'; exit 0; fi
            if [ "$1 $2" = "image rm" ]; then echo "$3" >> /tmp/prune/removed; [ "$3" != "repo:1.2.0" ]; exit $?; fi
            exit 1
            """;
        var script = await File.ReadAllTextAsync(Path.Combine(AppsImageFixture.PackagingDirectory, "compose", "prune-old-images.sh"));

        await image.ScratchContainer.ExecAsync(["mkdir", "-p", "/tmp/prune/bin"]);
        await image.ScratchContainer.CopyAsync(Encoding.UTF8.GetBytes(stub.Replace("\r", "")), "/tmp/prune/bin/docker", fileMode: (UnixFileModes)Convert.ToInt32("755", 8));
        await image.ScratchContainer.CopyAsync(Encoding.UTF8.GetBytes(script.Replace("\r", "")), "/tmp/prune/prune.sh", fileMode: (UnixFileModes)Convert.ToInt32("755", 8));

        var run = await image.ScratchContainer.ExecAsync(["sh", "-c", "PATH=/tmp/prune/bin:$PATH APPS_IMAGE_REPOSITORY=repo sh /tmp/prune/prune.sh"]);
        var removed = await image.ScratchContainer.ExecAsync(["cat", "/tmp/prune/removed"]);

        run.ExitCode.Should().Be(0, "removing 1.2.0 failed in the stub, and that must not fail the update");
        removed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().Equal("repo:1.2.0", "repo:0.1.0");
    }

    [Fact]
    public async Task PublishScript_RefusesAFileNameThatIsNotAPlainName()
    {
        var dir = Directory.CreateTempSubdirectory("apps-publish-");
        try
        {
            var bad = Path.Combine(dir.FullName, "has space.exe");
            await File.WriteAllBytesAsync(bad, [1, 2, 3]);

            var publish = () => AppsImageFixture.Publish("--release", "1.0.0", "--dry-run", "--windows", bad, "--windows-version", "1.0.0");

            await publish.Should().ThrowAsync<Exception>().Where(e => e.Message.Contains("file name not allowed"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
