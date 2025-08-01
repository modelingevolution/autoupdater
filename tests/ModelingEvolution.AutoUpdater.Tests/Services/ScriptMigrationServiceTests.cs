using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    public class ScriptMigrationServiceTests
    {
        private readonly ISshService _sshService = Substitute.For<ISshService>();
        private readonly ILogger<ScriptMigrationService> _logger = Substitute.For<ILogger<ScriptMigrationService>>();
        private readonly ScriptMigrationService _service;

        public ScriptMigrationServiceTests()
        {
            _service = new ScriptMigrationService(_sshService, _logger);
        }

        [Fact]
        public void Constructor_WithNullSshService_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = () => new ScriptMigrationService(null!, _logger);
            act.Should().Throw<ArgumentNullException>().WithParameterName("sshService");
        }

        [Fact]
        public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = () => new ScriptMigrationService(_sshService, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }

        [Fact]
        public async Task FilterScriptsForMigrationAsync_WithInvalidTargetVersion_ShouldReturnEmpty()
        {
            // Arrange
            var scripts = new[]
            {
                new MigrationScript("up-1.0.0.sh", "/path/up-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Up)
            };

            // Act
            var result = await _service.FilterScriptsForMigrationAsync(scripts, null, new PackageVersion("invalid-version"));

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task FilterScriptsForMigrationAsync_WithInvalidFromVersion_ShouldTreatAsInitialMigration()
        {
            // Arrange
            var scripts = new[]
            {
                new MigrationScript("up-1.0.0.sh", "/path/up-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Up),
                new MigrationScript("up-2.0.0.sh", "/path/up-2.0.0.sh", new PackageVersion("2.0.0"), MigrationDirection.Up),
                new MigrationScript("up-3.0.0.sh", "/path/up-3.0.0.sh", new PackageVersion("3.0.0"), MigrationDirection.Up)
            };

            // Act - invalid version is normalized to Empty, so treated as initial migration
            var result = await _service.FilterScriptsForMigrationAsync(scripts, new PackageVersion("invalid-version"), new PackageVersion("2.0.0"));

            // Assert - should include all scripts up to target version
            result.Should().HaveCount(2);
            var filteredScripts = result.ToList();
            filteredScripts[0].Version.Should().Be(new PackageVersion("1.0.0"));
            filteredScripts[1].Version.Should().Be(new PackageVersion("2.0.0"));
        }
        [Fact]
        public async Task FilterScriptsForMigrationAsync_WithSingle_ShouldSingle()
        {
            // Arrange
            var scripts = new[]
            {
                new MigrationScript("up-1.0.0.sh", "/path/up-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Up)
            };

            // Act
            var result = await _service.FilterScriptsForMigrationAsync(scripts, PackageVersion.Empty, new PackageVersion("1.0.0"));

            // Assert
            result.Should().HaveCount(1);
        }
        [Fact]
        public async Task FilterScriptsForMigrationAsync_FromInitialToTarget_ShouldIncludeAllValidUpScripts()
        {
            // Arrange
            var scripts = new[]
            {
                new MigrationScript("up-0.5.0.sh", "/path/up-0.5.0.sh", new PackageVersion("0.5.0"), MigrationDirection.Up),
                new MigrationScript("up-1.0.0.sh", "/path/up-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Up),
                new MigrationScript("up-1.5.0.sh", "/path/up-1.5.0.sh", new PackageVersion("1.5.0"), MigrationDirection.Up),
                new MigrationScript("up-2.0.0.sh", "/path/up-2.0.0.sh", new PackageVersion("2.0.0"), MigrationDirection.Up),
                new MigrationScript("up-1.2.0.sh", "/path/up-1.2.0.sh", new PackageVersion("1.2.0"), MigrationDirection.Up), // not executable
                new MigrationScript("down-1.0.0.sh", "/path/down-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Down) // should be ignored for forward migration
            };

            // Act
            var result = await _service.FilterScriptsForMigrationAsync(scripts, null, new PackageVersion("1.5.0"));

            // Assert
            result.Should().HaveCount(4);
            var filteredScripts = result.ToList();
            filteredScripts[0].Version.Should().Be(new PackageVersion("0.5.0"));
            filteredScripts[1].Version.Should().Be(new PackageVersion("1.0.0"));
            filteredScripts[2].Version.Should().Be(new PackageVersion("1.2.0"));
        }

        [Fact]
        public async Task FilterScriptsForMigrationAsync_FromVersionToTarget_ShouldExcludeOlderVersions()
        {
            // Arrange
            var scripts = new[]
            {
                new MigrationScript("up-0.5.0.sh", "/path/up-0.5.0.sh", new PackageVersion("0.5.0"), MigrationDirection.Up),
                new MigrationScript("up-1.0.0.sh", "/path/up-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Up),
                new MigrationScript("up-1.2.0.sh", "/path/up-1.2.0.sh", new PackageVersion("1.2.0"), MigrationDirection.Up),
                new MigrationScript("up-1.5.0.sh", "/path/up-1.5.0.sh", new PackageVersion("1.5.0"), MigrationDirection.Up)
            };

            // Act
            var result = await _service.FilterScriptsForMigrationAsync(scripts, new PackageVersion("1.0.0"), new PackageVersion("1.5.0"));

            // Assert
            result.Should().HaveCount(2);
            var filteredScripts = result.ToList();
            filteredScripts[0].Version.Should().Be(new PackageVersion("1.2.0"));
            filteredScripts[1].Version.Should().Be(new PackageVersion("1.5.0"));
        }

        

        [Fact]
        public async Task ExecuteScriptsAsync_WithValidScripts_ShouldCallMakeExecutableAndExecute()
        {
            // Arrange
            var scripts = new[]
            {
                new MigrationScript("up-1.0.0.sh", "/path/up-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Up),
                new MigrationScript("up-1.1.0.sh", "/path/up-1.1.0.sh", new PackageVersion("1.1.0"), MigrationDirection.Up)
            };

            _sshService.ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<string>())
                      .Returns(new SshCommandResult("cmd", "success"));

            // Act
            await _service.ExecuteScriptsAsync(scripts, "/test");

            // Assert
            await _sshService.Received(2).ExecuteCommandAsync(Arg.Is<string>(cmd => cmd.StartsWith("sudo bash")), "/test");
        }

       

        [Fact]
        public async Task ExecuteScriptsAsync_WithEmptyScriptList_ShouldNotExecuteAnything()
        {
            // Arrange
            var scripts = Enumerable.Empty<MigrationScript>();

            // Act
            await _service.ExecuteScriptsAsync(scripts, "/test");

            // Assert
            await _sshService.DidNotReceive().ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<string>());
        }

        [Fact]
        public async Task ValidateScriptAsync_WithValidScript_ShouldReturnTrue()
        {
            // Arrange
            const string scriptPath = "/path/up-1.0.0.sh";
            _sshService.IsExecutableAsync(Arg.Any<string>()).Returns(true);

            // Act
            var result = await _service.ValidateScriptAsync(scriptPath);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public async Task ValidateScriptAsync_WithInvalidNamePattern_ShouldReturnFalse()
        {
            // Act
            var result = await _service.ValidateScriptAsync("/path/invalid-name.sh");

            // Assert
            result.Should().BeFalse();
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task ValidateScriptAsync_WithInvalidPath_ShouldReturnFalse(string? scriptPath)
        {
            // Act
            var result = await _service.ValidateScriptAsync(scriptPath!);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public async Task FilterScriptsForMigrationAsync_RollbackScenario_ShouldUseDownScriptsInReverseOrder()
        {
            // Arrange
            var scripts = new[]
            {
                new MigrationScript("up-1.0.0.sh", "/path/up-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Up),
                new MigrationScript("up-1.5.0.sh", "/path/up-1.5.0.sh", new PackageVersion("1.5.0"), MigrationDirection.Up),
                new MigrationScript("down-1.0.0.sh", "/path/down-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Down),
                new MigrationScript("down-1.5.0.sh", "/path/down-1.5.0.sh", new PackageVersion("1.5.0"), MigrationDirection.Down),
                new MigrationScript("down-2.0.0.sh", "/path/down-2.0.0.sh", new PackageVersion("2.0.0"), MigrationDirection.Down)
            };

            // Previously executed UP scripts (simulates that 1.0.0 and 1.5.0 were applied)
            var executedVersions = ImmutableSortedSet.Create(new PackageVersion("1.0.0"), new PackageVersion("1.5.0"));

            // Act - rollback from 1.5.0 to 1.0.0
            var result = await _service.FilterScriptsForMigrationAsync(scripts, new PackageVersion("1.5.0"), new PackageVersion("1.0.0"), executedVersions);

            // Assert
            result.Should().HaveCount(1);
            var filteredScripts = result.ToList();
            filteredScripts[0].Should().Be(scripts[3]); // down-1.5.0.sh
            filteredScripts[0].Direction.Should().Be(MigrationDirection.Down);
        }

        [Fact]
        public async Task FilterScriptsForMigrationAsync_SameVersion_ShouldReturnEmpty()
        {
            // Arrange
            var scripts = new[]
            {
                new MigrationScript("up-1.0.0.sh", "/path/up-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Up),
                new MigrationScript("down-1.0.0.sh", "/path/down-1.0.0.sh", new PackageVersion("1.0.0"), MigrationDirection.Down)
            };

            // Act
            var result = await _service.FilterScriptsForMigrationAsync(scripts, new PackageVersion("1.0.0"), new PackageVersion("1.0.0"));

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task ValidateScriptAsync_WithDownScript_ShouldReturnTrue()
        {
            // Arrange
            const string scriptPath = "/path/down-1.0.0.sh";
            _sshService.IsExecutableAsync(Arg.Any<string>()).Returns(true);

            // Act
            var result = await _service.ValidateScriptAsync(scriptPath);

            // Assert
            result.Should().BeTrue();
        }
    }
}