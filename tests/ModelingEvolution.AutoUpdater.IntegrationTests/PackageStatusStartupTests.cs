using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;
using ModelingEvolution.AutoUpdater.Host.Models;
using ModelingEvolution.AutoUpdater.Host.Services;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using Xunit;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

/// <summary>
/// Regression: after a restart every page showed "Version unknown", because the package read model was created lazily, after
/// the startup version checks had published their results. Starts the hosted services the way the generic host does, with
/// no page ever resolving the read model first.
/// </summary>
public class PackageStatusStartupTests
{
    [Fact]
    public async Task StartupVersionCheck_ReachesTheReadModel_WithoutAPageResolvingItFirst()
    {
        await using var provider = BuildAndStart(hub: null, out var started);
        await started;

        var state = provider.GetRequiredService<PackageStateReadModel>().GetPackageState(new PackageName("rocketwelder-apps"))!;
        state.CurrentVersion.Should().Be(PackageVersion.Parse("1.0.0"));
        state.IsUpgradeAvailable.Should().BeTrue();
        state.StatusText.Should().NotBe("Version unknown");
        state.IsCheckingForUpdates.Should().BeFalse();
    }

    [Fact]
    public async Task StartupVersionCheck_ThatThrowsBeforeItsResult_DoesNotLeaveThePackageChecking()
    {
        await using var provider = BuildAndStart(new ResultLosingEventHub(), out var started);
        await started;

        var state = provider.GetRequiredService<PackageStateReadModel>().GetPackageState(new PackageName("rocketwelder-apps"))!;
        state.IsCheckingForUpdates.Should().BeFalse();
        state.OperationMessage.Should().Be("Version check failed. Details are in the AutoUpdater log.");
    }

    /// <summary>Delivers subscriptions normally but fails to publish the version-check result, as a crashed check would.</summary>
    private sealed class ResultLosingEventHub : IEventHub
    {
        private readonly EventHub _inner = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<EventHub>.Instance);

        public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : class =>
            eventData is VersionCheckCompletedEvent ? throw new InvalidOperationException("lost") : _inner.PublishAsync(eventData);

        public IEventSubscription Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class => _inner.Subscribe(handler);
        public IEventSubscription Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class => _inner.Subscribe(handler);
        public void Unsubscribe(IEventSubscription subscription) => _inner.Unsubscribe(subscription);
    }

    private static ServiceProvider BuildAndStart(IEventHub? hub, out Task started)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packages:0:RepositoryLocation"] = "/data/rocketwelder-apps",
                ["Packages:0:RepositoryUrl"] = "https://example.invalid/rocketwelder-apps-compose.git",
            })
            .Build();

        var git = Substitute.For<IGitService>();
        git.IsGitRepository(Arg.Any<string>()).Returns(true);
        git.GetAvailableVersionsAsync(Arg.Any<string>()).Returns([PackageVersion.Parse("1.1.0")]);

        var deploymentState = Substitute.For<IDeploymentStateProvider>();
        deploymentState.GetDeploymentStateAsync(Arg.Any<string>())
            .Returns(new DeploymentState(PackageVersion.Parse("1.0.0"), DateTime.UtcNow));

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IEventHub>(sp => hub ?? ActivatorUtilities.CreateInstance<EventHub>(sp))
            .AddSingleton<DockerComposeConfigurationModel>()
            .AddSingleton<PackageStateReadModel>()
            .AddSingleton(git)
            .AddSingleton(deploymentState)
            .AddSingleton(Substitute.For<IScriptMigrationService>())
            .AddSingleton(Substitute.For<ISshConnectionManager>())
            .AddSingleton(Substitute.For<IDockerComposeService>())
            .AddSingleton(Substitute.For<IBackupService>())
            .AddSingleton(Substitute.For<IHealthCheckService>())
            .AddSingleton(Substitute.For<IProgressService>())
            .AddSingleton<UpdateHost>()
            .AddHostedService<PackageStatusInitializationService>();
        var provider = services.BuildServiceProvider();

        // As the generic host: resolve every hosted service, then start them.
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        started = StartAll(hostedServices);
        return provider;

        static async Task StartAll(List<IHostedService> hostedServices)
        {
            foreach (var hosted in hostedServices) await hosted.StartAsync(CancellationToken.None);
        }
    }
}
