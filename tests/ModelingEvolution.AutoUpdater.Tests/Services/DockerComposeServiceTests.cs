using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Threading.Tasks;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    public class DockerComposeServiceTests
    {
        private readonly ISshService _sshService = Substitute.For<ISshService>();
        private readonly ILogger<DockerComposeService> _logger = Substitute.For<ILogger<DockerComposeService>>();
        private readonly DockerComposeService _service;

        public DockerComposeServiceTests()
        {
            _service = new DockerComposeService(_sshService, _logger);
        }

        [Fact]
        public void Constructor_WithNullSshService_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = () => new DockerComposeService(null!, _logger);
            act.Should().Throw<ArgumentNullException>().WithParameterName("sshService");
        }

        [Fact]
        public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = () => new DockerComposeService(_sshService, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task GetComposeFilesForArchitectureAsync_WithInvalidDirectoryPath_ShouldThrowArgumentException(string? directoryPath)
        {
            // Act & Assert
            var act = async () => await _service.GetComposeFilesForArchitectureAsync(directoryPath!, "x64");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("directoryPath");
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task GetComposeFilesForArchitectureAsync_WithInvalidArchitecture_ShouldThrowArgumentException(string? architecture)
        {
            // Act & Assert
            var act = async () => await _service.GetComposeFilesForArchitectureAsync("/path", architecture!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("architecture");
        }

        [Fact]
        public async Task GetComposeFilesForArchitectureAsync_WithBaseFileOnly_ShouldReturnBaseFile()
        {
            // Arrange
            const string directoryPath = "/app";
            const string architecture = "x64";
            
            _sshService.FileExistsAsync("/app/docker-compose.yml").Returns(true);
            _sshService.FileExistsAsync("/app/docker-compose.x64.yml").Returns(false);

            // Act
            var result = await _service.GetComposeFilesForArchitectureAsync(directoryPath, architecture);

            // Assert
            result.Should().HaveCount(1);
            result[0].Should().Be("docker-compose.yml");
        }

        [Fact]
        public async Task GetComposeFilesForArchitectureAsync_WithBothFiles_ShouldReturnBothFiles()
        {
            // Arrange
            const string directoryPath = "/app";
            const string architecture = "arm64";
            
            _sshService.FileExistsAsync("/app/docker-compose.yml").Returns(true);
            _sshService.FileExistsAsync("/app/docker-compose.arm64.yml").Returns(true);

            // Act
            var result = await _service.GetComposeFilesForArchitectureAsync(directoryPath, architecture);

            // Assert
            result.Should().HaveCount(2);
            result[0].Should().Be("docker-compose.yml");
            result[1].Should().Be("docker-compose.arm64.yml");
        }

        [Fact]
        public async Task GetComposeFilesForArchitectureAsync_WithNoFiles_ShouldReturnEmpty()
        {
            // Arrange
            const string directoryPath = "/app";
            const string architecture = "x64";
            
            _sshService.FileExistsAsync(Arg.Any<string>()).Returns(false);

            // Act
            var result = await _service.GetComposeFilesForArchitectureAsync(directoryPath, architecture);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task GetComposeFilesForArchitectureAsync_WithArchFileOnly_ShouldReturnArchFile()
        {
            // Arrange
            const string directoryPath = "/app";
            const string architecture = "x64";
            
            _sshService.FileExistsAsync("/app/docker-compose.yml").Returns(false);
            _sshService.FileExistsAsync("/app/docker-compose.x64.yml").Returns(true);

            // Act
            var result = await _service.GetComposeFilesForArchitectureAsync(directoryPath, architecture);

            // Assert
            result.Should().HaveCount(1);
            result[0].Should().Be("docker-compose.x64.yml");
        }

        [Fact]
        public async Task StartServicesAsync_WithNullComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.StartServicesAsync(null!, "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }

        [Fact]
        public async Task StartServicesAsync_WithEmptyComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.StartServicesAsync(Array.Empty<string>(), "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task StartServicesAsync_WithInvalidWorkingDirectory_ShouldThrowArgumentException(string? workingDirectory)
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml" };

            // Act & Assert
            var act = async () => await _service.StartServicesAsync(composeFiles, workingDirectory!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("workingDirectory");
        }

        [Fact]
        public async Task StartServicesAsync_WithSingleFile_ShouldExecuteCorrectCommand()
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "docker-compose -f \"docker-compose.yml\" up -d";
            
            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand,"Started successfully"));

            // Act
            await _service.StartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task StartServicesAsync_WithMultipleFiles_ShouldExecuteCorrectCommand()
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.x64.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "docker-compose -f \"docker-compose.yml\" -f \"docker-compose.x64.yml\" up -d";
            
            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand, "Started successfully"));

            // Act
            await _service.StartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task StopServicesAsync_WithValidParameters_ShouldExecuteCorrectCommand()
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.arm64.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "docker-compose -f \"docker-compose.yml\" -f \"docker-compose.arm64.yml\" down";
            
            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand,"Stopped successfully"));

            // Act
            await _service.StopServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        
        [Fact]
        public async Task PullImagesAsync_WithValidParameters_ShouldExecuteCorrectCommand()
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "docker-compose -f \"docker-compose.yml\" pull";
            
            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand,"Pulled successfully"));

            // Act
            await _service.PullImagesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task GetServicesStatusAsync_WithValidParameters_ShouldExecuteCorrectCommand()
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "docker-compose -f \"docker-compose.yml\" ps";
            const string expectedOutput = "service1  running\nservice2  stopped";
            
            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand, expectedOutput));

            // Act
            var result = await _service.GetServicesStatusAsync(composeFiles, workingDirectory);

            // Assert
            result.Should().Be(expectedOutput);
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task RestartServicesAsync_WithValidParameters_ShouldExecuteCorrectCommand()
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.x64.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "docker-compose -f \"docker-compose.yml\" -f \"docker-compose.x64.yml\" restart";
            
            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand, "Restarted successfully"));

            // Act
            await _service.RestartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task StartServicesAsync_WhenSshServiceThrows_ShouldPropagateException()
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            var expectedException = new Exception("SSH command failed");
            
            _sshService.ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<string>()).Throws(expectedException);

            // Act & Assert
            var act = async () => await _service.StartServicesAsync(composeFiles, workingDirectory);
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("SSH command failed");
        }

        [Fact]
        public async Task StopServicesAsync_WithNullComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.StopServicesAsync(null!, "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }

        [Fact]
        public async Task PullImagesAsync_WithEmptyComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.PullImagesAsync(Array.Empty<string>(), "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task GetServicesStatusAsync_WithInvalidWorkingDirectory_ShouldThrowArgumentException(string? workingDirectory)
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml" };

            // Act & Assert
            var act = async () => await _service.GetServicesStatusAsync(composeFiles, workingDirectory!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("workingDirectory");
        }

        [Fact]
        public async Task RestartServicesAsync_WithNullComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.RestartServicesAsync(null!, "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }
    }
}