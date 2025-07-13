using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    public class UpdateHostTests
    {
        private readonly IGitService _gitService = Substitute.For<IGitService>();
        private readonly IScriptMigrationService _scriptService = Substitute.For<IScriptMigrationService>();
        private readonly ISshService _sshService = Substitute.For<ISshService>();
        private readonly IDockerComposeService _dockerService = Substitute.For<IDockerComposeService>();
        private readonly ILogger<UpdateHost> _logger = Substitute.For<ILogger<UpdateHost>>();

        [Fact]
        public async Task UpdateAsync_WithNewVersion_ExecutesMigrationScripts()
        {
            // Arrange
            var config = CreateTestConfiguration();
            _gitService.GetCurrentVersionAsync(Arg.Any<string>())
                      .Returns("1.0.0");
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new Version(1, 1, 0)) });

            var migrationScripts = new[]
            {
                new MigrationScript("host-1.0.1.sh", "/path/host-1.0.1.sh", new Version(1, 0, 1), true)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), "1.0.0", "1.1.0")
                         .Returns(migrationScripts);

            _sshService.GetArchitectureAsync()
                      .Returns("x64");

            _dockerService.GetComposeFilesForArchitectureAsync(Arg.Any<string>(), "x64")
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });

            var updateHost = new UpdateHost(_gitService, _scriptService, _sshService, _dockerService, _logger);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Success.Should().BeTrue();
            result.ExecutedScripts.Should().HaveCount(1);
            result.ExecutedScripts.Should().Contain("host-1.0.1.sh");
            
            await _scriptService.Received(1).ExecuteScriptsAsync(migrationScripts, Arg.Any<string>());
            await _dockerService.Received(1).StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
        }

        [Fact]
        public async Task UpdateAsync_WithNoNewVersion_DoesNotUpdate()
        {
            // Arrange
            var config = CreateTestConfiguration();
            _gitService.GetCurrentVersionAsync(Arg.Any<string>())
                      .Returns("1.0.0");
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.0.0", new Version(1, 0, 0)) });

            var updateHost = new UpdateHost(_gitService, _scriptService, _sshService, _dockerService, _logger);

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
            _gitService.GetCurrentVersionAsync(Arg.Any<string>())
                      .Returns("1.0.0");
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new Version(1, 1, 0)) });

            var migrationScripts = new[]
            {
                new MigrationScript("host-1.0.1.sh", "/path/host-1.0.1.sh", new Version(1, 0, 1), true)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), "1.0.0", "1.1.0")
                         .Returns(migrationScripts);
            _scriptService.When(x => x.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>()))
                         .Do(x => throw new Exception("Script execution failed"));

            _sshService.GetArchitectureAsync()
                      .Returns("x64");

            var updateHost = new UpdateHost(_gitService, _scriptService, _sshService, _dockerService, _logger);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Script execution failed");
            
            await _dockerService.DidNotReceive().StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
        }

        private static DockerComposeConfiguration CreateTestConfiguration()
        {
            return new DockerComposeConfiguration
            {
                RepositoryLocation = "/test/repo",
                RepositoryUrl = "https://github.com/test/repo.git",
                DockerComposeDirectory = "./"
            };
        }
    }
}