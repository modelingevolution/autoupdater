using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    public class UpdateHostTests
    {
        private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
        private readonly IGitService _gitService = Substitute.For<IGitService>();
        private readonly IScriptMigrationService _scriptService = Substitute.For<IScriptMigrationService>();
        private readonly ISshConnectionManager _sshConnectionManager = Substitute.For<ISshConnectionManager>();
        private readonly IDockerComposeService _dockerService = Substitute.For<IDockerComposeService>();
        private readonly IDeploymentStateProvider _deploymentStateProvider = Substitute.For<IDeploymentStateProvider>();
        private readonly ILogger<UpdateHost> _logger = Substitute.For<ILogger<UpdateHost>>();

        [Fact]
        public async Task UpdateAsync_WithNewVersion_ExecutesMigrationScripts()
        {
            // Arrange
            var config = CreateTestConfiguration();
            _deploymentStateProvider.GetCurrentVersionAsync(Arg.Any<string>())
                      .Returns("1.0.0");
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new Version(1, 1, 0)) });

            var migrationScripts = new[]
            {
                new MigrationScript("up-1.0.1.sh", "/path/up-1.0.1.sh", new Version(1, 0, 1), MigrationDirection.Up, true)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), "1.0.0", "1.1.0")
                         .Returns(migrationScripts);

            var mockSshService = Substitute.For<ISshService>();
            mockSshService.GetArchitectureAsync().Returns("x64");
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService);

            _dockerService.GetComposeFilesForArchitectureAsync(Arg.Any<string>(), "x64")
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Success.Should().BeTrue();
            result.ExecutedScripts.Should().HaveCount(1);
            result.ExecutedScripts.Should().Contain("up-1.0.1.sh");
            
            await _scriptService.Received(1).ExecuteScriptsAsync(migrationScripts, Arg.Any<string>());
            await _dockerService.Received(1).StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
        }

        [Fact]
        public async Task UpdateAsync_WithNoNewVersion_DoesNotUpdate()
        {
            // Arrange
            var config = CreateTestConfiguration();
            _deploymentStateProvider.GetCurrentVersionAsync(Arg.Any<string>())
                      .Returns("1.0.0");
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.0.0", new Version(1, 0, 0)) });

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Success.Should().BeTrue();
            result.ExecutedScripts.Should().BeEmpty();
            
            await _scriptService.DidNotReceive().ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>());
            await _dockerService.DidNotReceive().StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
        }

        [Fact]
        public async Task UpdateAsync_WithScriptFailure_ReturnsFailure()
        {
            // Arrange
            var config = CreateTestConfiguration();
            _deploymentStateProvider.GetCurrentVersionAsync(Arg.Any<string>())
                      .Returns("1.0.0");
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new Version(1, 1, 0)) });

            var migrationScripts = new[]
            {
                new MigrationScript("up-1.0.1.sh", "/path/up-1.0.1.sh", new Version(1, 0, 1), MigrationDirection.Up, true)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), "1.0.0", "1.1.0")
                         .Returns(migrationScripts);
            _scriptService.When(x => x.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>()))
                         .Do(x => throw new Exception("Script execution failed"));

            var mockSshService2 = Substitute.For<ISshService>();
            mockSshService2.GetArchitectureAsync().Returns("x64");
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService2);

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Script execution failed");
            
            await _dockerService.DidNotReceive().StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
        }

        private static DockerComposeConfiguration CreateTestConfiguration()
        {
            var tempDir = Path.GetTempPath();
            return new DockerComposeConfiguration
            {
                RepositoryLocation = Path.Combine(tempDir, "test-repo"),
                RepositoryUrl = "https://github.com/test/repo.git",
                DockerComposeDirectory = "./"
            };
        }
    }
}