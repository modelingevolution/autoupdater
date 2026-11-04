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
    public const string WindowsFile = "RocketWelderClient-Setup.exe";
    public const string AndroidFile = "native-player-kiosk-1.0.0-arm64-v8a.apk";
    public const string LinuxFile = "native-player_1.0.0_arm64.deb";

    private readonly string _workDir = Directory.CreateTempSubdirectory("apps-image-").FullName;
    private IContainer _healthy = null!;
    private IContainer _tampered = null!;

    public string Tag { get; } = $"rocketwelder-apps-test:{Guid.NewGuid():N}";
    public byte[] WindowsBytes { get; } = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
    public HttpClient Healthy { get; private set; } = null!;
    public HttpClient Tampered { get; private set; } = null!;
    /// <summary>Its root filesystem is writable and its /tmp allows exec, unlike the compose-restricted one.</summary>
    public IContainer ScratchContainer => _tampered;
    public int HealthyPort => _healthy.GetMappedPublicPort(ContainerPort);

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
            .WithTmpfsMount("/tmp")
            .WithCreateParameterModifier(p =>
            {
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

        await Task.WhenAll(_healthy.StartAsync(), _tampered.StartAsync());

        Healthy = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HealthyPort}/") };
        Tampered = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_tampered.GetMappedPublicPort(ContainerPort)}/") };
    }

    public async Task DisposeAsync()
    {
        Healthy?.Dispose();
        Tampered?.Dispose();
        if (_healthy != null) await _healthy.DisposeAsync();
        if (_tampered != null) await _tampered.DisposeAsync();
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

    private static string Bash =>
        Environment.GetEnvironmentVariable("APPS_TEST_BASH")
        ?? (OperatingSystem.IsWindows()
            // Git Bash, wherever it is installed. A plain "bash" on Windows can resolve to WSL.
            ? Environment.GetLogicalDrives().Select(d => Path.Combine(d, @"Program Files\Git\bin\bash.exe")).First(File.Exists)
            : "bash");

    /// <summary>Runs publish-apps.sh. Paths go through the script's working directory so that Git Bash can resolve them.</summary>
    public static async Task<BufferedCommandResult> Publish(params string[] args)
    {
        var converted = args.Select(a => a.Contains('\\') ? a.Replace('\\', '/') : a);
        return await Cli.Wrap(Bash)
            .WithWorkingDirectory(PackagingDirectory)
            .WithArguments(new[] { "./publish-apps.sh" }.Concat(converted))
            .ExecuteBufferedAsync();
    }
}

public sealed class AppsImageIntegrationTests(AppsImageFixture image) : IClassFixture<AppsImageFixture>
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>One raw request, because HttpClient would normalise a traversal away before it reached the server.</summary>
    private async Task<string> RawGet(string path)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", image.HealthyPort);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
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
    [InlineData(AppsImageFixture.WindowsFile, "application/octet-stream")]
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
    [InlineData("/files/.hidden")]
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
