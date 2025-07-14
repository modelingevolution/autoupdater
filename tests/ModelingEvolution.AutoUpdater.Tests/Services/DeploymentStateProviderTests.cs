using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Threading.Tasks;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    public class DeploymentStateProviderTests
    {
        private readonly ISshService _sshService;
        private readonly ILogger<DeploymentStateProvider> _logger;
        private readonly DeploymentStateProvider _deploymentStateProvider;

        public DeploymentStateProviderTests()
        {
            _sshService = Substitute.For<ISshService>();
            _logger = Substitute.For<ILogger<DeploymentStateProvider>>();
            _deploymentStateProvider = new DeploymentStateProvider(_sshService, _logger);
        }

        [Fact]
        public void Constructor_WithNullSshService_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = () => new DeploymentStateProvider(null!, _logger);
            act.Should().Throw<ArgumentNullException>().WithParameterName("sshService");
        }

        [Fact]
        public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = () => new DeploymentStateProvider(_sshService, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }

        [Fact]
        public async Task GetCurrentVersionAsync_WithNonExistentStateFile_ShouldReturnNull()
        {
            // Arrange
            var deploymentPath = "/test/path";
            _sshService.FileExistsAsync(Arg.Any<string>()).Returns(false);

            // Act
            var result = await _deploymentStateProvider.GetCurrentVersionAsync(deploymentPath);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetCurrentVersionAsync_WithValidStateFile_ShouldReturnVersion()
        {
            // Arrange
            var deploymentPath = "/test/path";
            var expectedVersion = "1.2.3";
            var stateJson = $@"{{""Version"":""{expectedVersion}"",""Updated"":""2023-01-01T00:00:00Z""}}";
            
            _sshService.FileExistsAsync(Arg.Any<string>()).Returns(true);
            _sshService.ReadFileAsync(Arg.Any<string>()).Returns(stateJson);

            // Act
            var result = await _deploymentStateProvider.GetCurrentVersionAsync(deploymentPath);

            // Assert
            result.Should().Be(expectedVersion);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task GetCurrentVersionAsync_WithInvalidDeploymentPath_ShouldThrowArgumentException(string deploymentPath)
        {
            // Act & Assert
            var result = await _deploymentStateProvider.GetCurrentVersionAsync(deploymentPath);
            result.Should().BeNull();

        }

        [Fact]
        public async Task SaveDeploymentStateAsync_WithValidInput_ShouldWriteStateFile()
        {
            // Arrange
            var deploymentPath = "/test/path";
            var state = new DeploymentState("1.2.3", DateTime.Now);
            _sshService.DirectoryExistsAsync(deploymentPath).Returns(true);

            // Act
            await _deploymentStateProvider.SaveDeploymentStateAsync(deploymentPath, state);

            // Assert
            await _sshService.Received(1).WriteFileAsync(Arg.Any<string>(), Arg.Any<string>());
        }

        [Fact]
        public async Task SaveDeploymentStateAsync_WithNonExistentDirectory_ShouldCreateDirectory()
        {
            // Arrange
            var deploymentPath = "/test/path";
            var state = new DeploymentState("1.2.3", DateTime.Now);
            _sshService.DirectoryExistsAsync(deploymentPath).Returns(false);

            // Act
            await _deploymentStateProvider.SaveDeploymentStateAsync(deploymentPath, state);

            // Assert
            await _sshService.Received(1).CreateDirectoryAsync(deploymentPath);
            await _sshService.Received(1).WriteFileAsync(Arg.Any<string>(), Arg.Any<string>());
        }

        [Fact]
        public async Task SaveDeploymentStateAsync_WithNullState_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = async () => await _deploymentStateProvider.SaveDeploymentStateAsync("/test/path", null!);
            await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("state");
        }

        [Fact]
        public async Task DeploymentStateExistsAsync_WithExistingFile_ShouldReturnTrue()
        {
            // Arrange
            var deploymentPath = "/test/path";
            _sshService.FileExistsAsync(Arg.Any<string>()).Returns(true);

            // Act
            var result = await _deploymentStateProvider.DeploymentStateExistsAsync(deploymentPath);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public async Task DeploymentStateExistsAsync_WithNonExistingFile_ShouldReturnFalse()
        {
            // Arrange
            var deploymentPath = "/test/path";
            _sshService.FileExistsAsync(Arg.Any<string>()).Returns(false);

            // Act
            var result = await _deploymentStateProvider.DeploymentStateExistsAsync(deploymentPath);

            // Assert
            result.Should().BeFalse();
        }
    }
}