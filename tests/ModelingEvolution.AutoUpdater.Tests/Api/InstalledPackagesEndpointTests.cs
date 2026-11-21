using System.Collections.Immutable;
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
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;

namespace ModelingEvolution.AutoUpdater.Tests.Api;

/// <summary>
/// GET /api/packages/installed returns installed state only (owner 2026-11-21: "just checking current state - this should
/// be extreamly quick"). It must never reach git or the update check; GET /api/packages keeps doing both.
/// </summary>
public class InstalledPackagesEndpointTests : IAsyncDisposable
{
    private const string RocketWelderPath = "/data/rocket-welder";
    private const string RocketWelderUrl = "https://github.com/example/rocket-welder-compose.git";
    private const string PlumberPath = "/data/plumber";
    private const string PlumberUrl = "https://github.com/example/plumber-compose.git";

    private readonly IDeploymentStateProvider _deploymentState = Substitute.For<IDeploymentStateProvider>();
    private readonly IDockerComposeService _compose = Substitute.For<IDockerComposeService>();
    private readonly IEventHub _eventHub = Substitute.For<IEventHub>();
    private WebApplication? _app;

    public InstalledPackagesEndpointTests()
    {
        // rocket-welder: installed at 1.2.3 and running; plumber: no deployment state, not in `compose ls`.
        _deploymentState.GetCurrentVersionAsync(Arg.Is<string>(p => p.Contains("/rocket-welder/"))).Returns("1.2.3");
        _deploymentState.GetCurrentVersionAsync(Arg.Is<string>(p => p.Contains("/plumber/"))).Returns((string?)null);
        _compose.GetDockerComposeStatusAsync().Returns(new Dictionary<PackageName, ComposeProjectStatus>
        {
            [new PackageName("rocket-welder")] = new("running(2)", ImmutableArray<string>.Empty, 2, 2)
        });
    }

    [Fact]
    public async Task GetInstalledPackagesAsync_NeverCallsGitOrTheUpdateCheck()
    {
        var git = new FailingGitService();
        var service = CreateService(git);

        await service.GetInstalledPackagesAsync();

        git.Calls.Should().BeEmpty("the installed-state read must not touch git (fetch, tags, clone)");
        // The update check (UpdateHost.CheckIsUpdateAvailable) reads GetDeploymentStateAsync and publishes VersionCheckCompletedEvent.
        await _deploymentState.DidNotReceiveWithAnyArgs().GetDeploymentStateAsync(default!);
        await _eventHub.DidNotReceiveWithAnyArgs().PublishAsync<VersionCheckCompletedEvent>(default!);
        // Current version: exactly one read per package.
        await _deploymentState.Received(1).GetCurrentVersionAsync("/var/docker/configuration/rocket-welder/./");
        await _deploymentState.Received(1).GetCurrentVersionAsync("/var/docker/configuration/plumber/./");
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
    public async Task GetInstalled_OverHttp_ReturnsTheInstalledStateJson()
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
        var plumber = packages.Single(p => p.GetProperty("name").GetString() == "plumber");
        plumber.GetProperty("currentVersion").ValueKind.Should().Be(JsonValueKind.Null);
        plumber.GetProperty("status").GetString().Should().Be("not-deployed");
        git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPackages_OverHttp_StillRunsTheUpdateCheckPerPackage()
    {
        // /api/packages is unchanged: per package it reads available versions from git and runs the update check.
        var git = Substitute.For<IGitService>();
        git.IsGitRepository(Arg.Any<string>()).Returns(true);
        git.GetAvailableVersionsAsync(Arg.Any<string>()).Returns(new[] { PackageVersion.Parse("1.3.0") });
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
        await _deploymentState.Received().GetDeploymentStateAsync("/var/docker/configuration/rocket-welder/./");
        await _eventHub.Received(2).PublishAsync(Arg.Any<VersionCheckCompletedEvent>());
    }

    private AutoUpdaterService CreateService(IGitService git)
    {
        var configuration = CreateConfiguration();
        var updateHost = new UpdateHost(configuration, NullLogger<UpdateHost>.Instance, git,
            Substitute.For<IScriptMigrationService>(), Substitute.For<ISshConnectionManager>(), _compose, _deploymentState,
            Substitute.For<IBackupService>(), Substitute.For<IHealthCheckService>(), Substitute.For<IProgressService>(), _eventHub);
        var packageManager = new PackageManager(
            new DockerComposeConfigurationModel(configuration, NullLogger<DockerComposeConfigurationModel>.Instance),
            updateHost, git, _deploymentState, Substitute.For<IBackupService>(), _compose,
            Substitute.For<ISshConnectionManager>(), NullLogger<PackageManager>.Instance);
        return new AutoUpdaterService(packageManager, Substitute.For<ISshConnectionManager>(), _compose,
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
