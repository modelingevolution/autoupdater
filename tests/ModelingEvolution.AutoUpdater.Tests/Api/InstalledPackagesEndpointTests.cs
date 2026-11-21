using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;
using ModelingEvolution.AutoUpdater.Host.Api.AutoUpdater;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ModelingEvolution.AutoUpdater.Tests.Api;

/// <summary>
/// GET /api/packages/installed returns installed state only (owner 2026-11-21: "just checking current state - this should
/// be extreamly quick"). It must never reach git or the update check; GET /api/packages keeps doing both.
/// A broken probe (compose ls or a version read failing) answers 503, never "not installed"; a missing state file is a
/// legitimate null version. The real DeploymentStateProvider and DockerComposeService run over a fake SSH service.
/// </summary>
public class InstalledPackagesEndpointTests : IAsyncDisposable
{
    private const string RocketWelderPath = "/data/rocket-welder";
    private const string RocketWelderUrl = "https://github.com/example/rocket-welder-compose.git";
    private const string PlumberPath = "/data/plumber";
    private const string PlumberUrl = "https://github.com/example/plumber-compose.git";
    private const string RocketWelderState = "/var/docker/configuration/rocket-welder/./deployment.state.json";
    private const string PlumberState = "/var/docker/configuration/plumber/./deployment.state.json";
    private const string ComposeLs = "sudo docker compose ls --format json";

    private readonly ISshService _ssh = Substitute.For<ISshService>();
    private readonly IEventHub _eventHub = Substitute.For<IEventHub>();
    private WebApplication? _app;

    public InstalledPackagesEndpointTests()
    {
        // rocket-welder: installed at 1.2.3 and running; plumber: no deployment state file, not in `compose ls`.
        var state = JsonSerializer.Serialize(new DeploymentState(PackageVersion.Parse("1.2.3"), DateTime.UtcNow));
        _ssh.ExecuteCommandAsync($"test -f {RocketWelderState}").Returns(new SshCommandResult($"test -f {RocketWelderState}", ""));
        _ssh.ReadFileAsync(RocketWelderState).Returns(state);
        _ssh.ExecuteCommandAsync($"test -f {PlumberState}").Returns(SshCommandResult.Failed($"test -f {PlumberState}", 1, "", ""));

        _ssh.ExecuteCommandAsync("sudo docker compose version")
            .Returns(new SshCommandResult("sudo docker compose version", "Docker Compose version v2.20.2"));
        _ssh.ExecuteCommandAsync(ComposeLs)
            .Returns(new SshCommandResult(ComposeLs, "[{\"Name\":\"rocket-welder\",\"Status\":\"running(2)\",\"ConfigFiles\":\"docker-compose.yml\"}]"));
    }

    [Fact]
    public async Task GetInstalledPackagesAsync_NeverCallsGitOrTheUpdateCheck()
    {
        var git = new FailingGitService();
        var service = CreateService(git);

        await service.GetInstalledPackagesAsync();

        git.Calls.Should().BeEmpty("the installed-state read must not touch git (fetch, tags, clone)");
        // The update check (UpdateHost.CheckIsUpdateAvailable) publishes VersionCheckCompletedEvent and reads the state file
        // a second time; exactly one read per package proves it did not run.
        await _eventHub.DidNotReceiveWithAnyArgs().PublishAsync<VersionCheckCompletedEvent>(default!);
        await _ssh.Received(1).ExecuteCommandAsync($"test -f {RocketWelderState}");
        await _ssh.Received(1).ReadFileAsync(RocketWelderState);
        await _ssh.Received(1).ExecuteCommandAsync($"test -f {PlumberState}");
        await _ssh.DidNotReceive().ReadFileAsync(PlumberState);
    }

    [Fact]
    public async Task GetInstalledPackagesAsync_ReturnsNameUrlVersionAndStatusPerPackage()
    {
        var service = CreateService(new FailingGitService());

        var response = await service.GetInstalledPackagesAsync();

        response.Packages.Should().HaveCount(2);
        var rw = response.Packages.Single(p => p.Name == new PackageName("rocket-welder"));
        rw.RepositoryUrl.Should().Be(RocketWelderUrl);
        rw.CurrentVersion.Should().Be(PackageVersion.Parse("1.2.3"));
        rw.Status.Should().Be("running");
        var plumber = response.Packages.Single(p => p.Name == new PackageName("plumber"));
        plumber.RepositoryUrl.Should().Be(PlumberUrl);
        plumber.CurrentVersion.Should().BeNull();
        plumber.Status.Should().Be("not-deployed");
    }

    [Fact]
    public async Task GetInstalled_OverHttp_ReturnsTheInstalledStateJson_AndAMissingStateFileIsANullVersion()
    {
        var git = new FailingGitService();
        var client = await StartAsync(git);

        var http = await client.GetAsync("/api/packages/installed");

        http.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await http.Content.ReadAsStringAsync());
        var packages = json.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        packages.Should().HaveCount(2);
        var rw = packages.Single(p => p.GetProperty("name").GetString() == "rocket-welder");
        rw.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("name", "repositoryUrl", "currentVersion", "status");
        rw.GetProperty("repositoryUrl").GetString().Should().Be(RocketWelderUrl);
        rw.GetProperty("currentVersion").GetString().Should().Be("1.2.3");
        rw.GetProperty("status").GetString().Should().Be("running");
        // plumber's state file does not exist (`test -f` exit 1): a legitimate "not installed", still 200.
        var plumber = packages.Single(p => p.GetProperty("name").GetString() == "plumber");
        plumber.GetProperty("currentVersion").ValueKind.Should().Be(JsonValueKind.Null);
        plumber.GetProperty("status").GetString().Should().Be("not-deployed");
        git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetInstalled_WhenComposeLsFails_Returns503_WhileApiPackagesKeepsItsOldAnswer()
    {
        _ssh.ExecuteCommandAsync(ComposeLs).Returns(SshCommandResult.Failed(ComposeLs, -1, "", "Connection lost"));
        var client = await StartAsync(WorkingGit());

        var installed = await client.GetAsync("/api/packages/installed");

        installed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemDetailAsync(installed)).Should().Contain("docker compose ls failed").And.Contain("Connection lost");

        // /api/packages is unchanged: a failed compose ls still reads as "not-deployed" with 200.
        var packages = await client.GetAsync("/api/packages");
        packages.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await packages.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("packages").EnumerateArray()
            .Select(p => p.GetProperty("status").GetString()).Should().AllBe("not-deployed");
    }

    [Fact]
    public async Task GetInstalled_WhenTheStateFileProbeFailsOverSsh_Returns503()
    {
        // SshService turns a lost connection or a timeout into exit -1; only exit 1 means "file does not exist".
        _ssh.ExecuteCommandAsync($"test -f {RocketWelderState}")
            .Returns(SshCommandResult.Failed($"test -f {RocketWelderState}", -1, "", "Command timed out after 600 seconds"));
        var client = await StartAsync(new FailingGitService());

        var http = await client.GetAsync("/api/packages/installed");

        http.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemDetailAsync(http)).Should().Contain("rocket-welder").And.Contain("timed out");
    }

    [Fact]
    public async Task GetInstalled_WhenTheStateFileReadFails_Returns503()
    {
        _ssh.ReadFileAsync(RocketWelderState)
            .Throws(new InvalidOperationException($"Failed to read file {RocketWelderState}: SCP channel closed"));
        var client = await StartAsync(new FailingGitService());

        var http = await client.GetAsync("/api/packages/installed");

        http.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemDetailAsync(http)).Should().Contain("rocket-welder").And.Contain("SCP channel closed");
    }

    [Fact]
    public async Task GetInstalled_WhenTheStateFileIsEmpty_Returns503()
    {
        _ssh.ReadFileAsync(RocketWelderState).Returns("  ");
        var client = await StartAsync(new FailingGitService());

        var http = await client.GetAsync("/api/packages/installed");

        http.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemDetailAsync(http)).Should().Contain("rocket-welder").And.Contain("is empty");
    }

    [Fact]
    public async Task GetPackages_OverHttp_StillRunsTheUpdateCheckPerPackage()
    {
        // /api/packages is unchanged: per package it reads available versions from git and runs the update check.
        var git = WorkingGit();
        var client = await StartAsync(git);

        var http = await client.GetAsync("/api/packages");

        http.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await http.Content.ReadAsStringAsync());
        var packages = json.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        packages.Should().HaveCount(2);
        var rw = packages.Single(p => p.GetProperty("name").GetString() == "rocket-welder");
        rw.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("name", "repositoryUrl", "currentVersion", "lastChecked", "status");
        rw.GetProperty("currentVersion").GetString().Should().Be("1.2.3");
        rw.GetProperty("status").GetString().Should().Be("running");
        await git.Received(1).FetchAsync(RocketWelderPath);
        await git.Received(1).FetchAsync(PlumberPath);
        // Current version + the update check's own state read: two reads per package, as before.
        await _ssh.Received(2).ReadFileAsync(RocketWelderState);
        await _eventHub.Received(2).PublishAsync(Arg.Any<VersionCheckCompletedEvent>());
    }

    private static IGitService WorkingGit()
    {
        var git = Substitute.For<IGitService>();
        git.IsGitRepository(Arg.Any<string>()).Returns(true);
        git.GetAvailableVersionsAsync(Arg.Any<string>()).Returns(new[] { PackageVersion.Parse("1.3.0") });
        return git;
    }

    private AutoUpdaterService CreateService(IGitService git)
    {
        var configuration = CreateConfiguration();
        var deploymentState = new DeploymentStateProvider(_ssh, NullLogger<DeploymentStateProvider>.Instance);
        var compose = new DockerComposeService(_ssh, NullLogger<DockerComposeService>.Instance);
        var updateHost = new UpdateHost(configuration, NullLogger<UpdateHost>.Instance, git,
            Substitute.For<IScriptMigrationService>(), Substitute.For<ISshConnectionManager>(), compose, deploymentState,
            Substitute.For<IBackupService>(), Substitute.For<IHealthCheckService>(), Substitute.For<IProgressService>(), _eventHub);
        var packageManager = new PackageManager(
            new DockerComposeConfigurationModel(configuration, NullLogger<DockerComposeConfigurationModel>.Instance),
            updateHost, git, deploymentState, Substitute.For<IBackupService>(), compose,
            Substitute.For<ISshConnectionManager>(), NullLogger<PackageManager>.Instance);
        return new AutoUpdaterService(packageManager, Substitute.For<ISshConnectionManager>(), compose,
            NullLogger<AutoUpdaterService>.Instance);
    }

    private async Task<HttpClient> StartAsync(IGitService git)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(CreateService(git));
        _app = builder.Build();
        _app.MapAutoUpdaterEndpoints();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static async Task<string> ProblemDetailAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("detail").GetString()!;
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Packages:0:RepositoryLocation"] = RocketWelderPath,
            ["Packages:0:RepositoryUrl"] = RocketWelderUrl,
            ["Packages:1:RepositoryLocation"] = PlumberPath,
            ["Packages:1:RepositoryUrl"] = PlumberUrl,
        })
        .Build();

    public async ValueTask DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
    }

    /// <summary>Fails on every call and records it: the installed-state path must never reach git.</summary>
    private sealed class FailingGitService : IGitService
    {
        public List<string> Calls { get; } = new();

        private Exception Fail(string member)
        {
            Calls.Add(member);
            return new InvalidOperationException($"IGitService.{member} must not be called by the installed-state read");
        }

        public Task<bool> CloneRepositoryAsync(string repositoryUrl, string targetPath) => throw Fail(nameof(CloneRepositoryAsync));
        public Task<bool> PullLatestAsync(string repositoryPath) => throw Fail(nameof(PullLatestAsync));
        public Task CheckoutVersionAsync(string repositoryPath, string version) => throw Fail(nameof(CheckoutVersionAsync));
        public Task<IReadOnlyList<PackageVersion>> GetAvailableVersionsAsync(string repositoryPath) => throw Fail(nameof(GetAvailableVersionsAsync));
        public bool IsGitRepository(string path) => throw Fail(nameof(IsGitRepository));
        public Task FetchAsync(string repositoryPath) => throw Fail(nameof(FetchAsync));
        public Task<bool> InitializeRepositoryAsync(string repositoryPath, string remoteUrl) => throw Fail(nameof(InitializeRepositoryAsync));
    }
}
