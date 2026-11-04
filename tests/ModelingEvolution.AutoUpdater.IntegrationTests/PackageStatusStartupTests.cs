using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelingEvolution.AutoUpdater.Common;
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
            .AddSingleton<IEventHub, EventHub>()
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
        await using var provider = services.BuildServiceProvider();

        // As the generic host: resolve every hosted service, then start them.
        foreach (var hosted in provider.GetServices<IHostedService>().ToList())
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        var state = provider.GetRequiredService<PackageStateReadModel>().GetPackageState(new PackageName("rocketwelder-apps"))!;
        state.CurrentVersion.Should().Be(PackageVersion.Parse("1.0.0"));
        state.IsUpgradeAvailable.Should().BeTrue();
        state.StatusText.Should().NotBe("Version unknown");
    }
}
