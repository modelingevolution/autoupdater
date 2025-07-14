using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    public class GitServiceTests : IDisposable
    {
        private readonly ILogger<GitService> _logger = Substitute.For<ILogger<GitService>>();
        private readonly GitService _gitService;
        private readonly string _tempDirectory;

        public GitServiceTests()
        {
            _gitService = new GitService(_logger);
            _tempDirectory = Path.Combine(Path.GetTempPath(), "GitServiceTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        [Fact]
        public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = () => new GitService(null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }

        [Fact]
        public async Task CloneRepositoryAsync_WithExistingDirectory_ShouldReturnFalse()
        {
            // Arrange
            var targetPath = Path.Combine(_tempDirectory, "existing");
            Directory.CreateDirectory(targetPath);

            // Act
            var result = await _gitService.CloneRepositoryAsync("https://github.com/test/repo.git", targetPath);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public async Task CloneRepositoryAsync_WithInvalidUrl_ShouldReturnFalse()
        {
            // Arrange
            var targetPath = Path.Combine(_tempDirectory, "clone-test");

            // Act
            var result = await _gitService.CloneRepositoryAsync("invalid-url", targetPath);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public async Task PullLatestAsync_WithNonGitRepository_ShouldReturnFalse()
        {
            // Arrange
            var nonGitPath = Path.Combine(_tempDirectory, "not-git");
            Directory.CreateDirectory(nonGitPath);

            // Act
            var result = await _gitService.PullLatestAsync(nonGitPath);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public async Task CheckoutVersionAsync_WithNonGitRepository_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var nonGitPath = Path.Combine(_tempDirectory, "not-git");
            Directory.CreateDirectory(nonGitPath);

            // Act & Assert
            var act = async () => await _gitService.CheckoutVersionAsync(nonGitPath, "v1.0.0");
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"Path {nonGitPath} is not a Git repository");
        }

        [Fact]
        public async Task GetAvailableVersionsAsync_WithNonGitRepository_ShouldReturnEmpty()
        {
            // Arrange
            var nonGitPath = Path.Combine(_tempDirectory, "not-git");
            Directory.CreateDirectory(nonGitPath);

            // Act
            var result = await _gitService.GetAvailableVersionsAsync(nonGitPath);

            // Assert
            result.Should().BeEmpty();
        }


        [Fact]
        public void IsGitRepository_WithNonExistentPath_ShouldReturnFalse()
        {
            // Arrange
            var nonExistentPath = Path.Combine(_tempDirectory, "does-not-exist");

            // Act
            var result = _gitService.IsGitRepository(nonExistentPath);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void IsGitRepository_WithDirectoryWithoutGit_ShouldReturnFalse()
        {
            // Arrange
            var testPath = Path.Combine(_tempDirectory, "no-git");
            Directory.CreateDirectory(testPath);

            // Act
            var result = _gitService.IsGitRepository(testPath);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void IsGitRepository_WithDirectoryWithGitFolder_ShouldReturnTrue()
        {
            // Arrange
            var testPath = Path.Combine(_tempDirectory, "with-git");
            Directory.CreateDirectory(testPath);
            Directory.CreateDirectory(Path.Combine(testPath, ".git"));

            // Act
            var result = _gitService.IsGitRepository(testPath);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public async Task FetchAsync_WithNonGitRepository_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var nonGitPath = Path.Combine(_tempDirectory, "not-git");
            Directory.CreateDirectory(nonGitPath);

            // Act & Assert
            var act = async () => await _gitService.FetchAsync(nonGitPath);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"Path {nonGitPath} is not a Git repository");
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task CloneRepositoryAsync_WithInvalidRepositoryUrl_ShouldReturnFalse(string? repositoryUrl)
        {
            // Arrange
            var targetPath = Path.Combine(_tempDirectory, "clone-invalid");

            // Act
            var result = await _gitService.CloneRepositoryAsync(repositoryUrl!, targetPath);

            // Assert
            result.Should().BeFalse();
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task PullLatestAsync_WithInvalidPath_ShouldReturnFalse(string? repositoryPath)
        {
            // Act
            var result = await _gitService.PullLatestAsync(repositoryPath!);

            // Assert
            result.Should().BeFalse();
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task CheckoutVersionAsync_WithInvalidPath_ShouldThrowInvalidOperationException(string? repositoryPath)
        {
            // Act & Assert
            var act = async () => await _gitService.CheckoutVersionAsync(repositoryPath!, "v1.0.0");
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task GetAvailableVersionsAsync_WithInvalidPath_ShouldReturnEmpty(string? repositoryPath)
        {
            // Act
            var result = await _gitService.GetAvailableVersionsAsync(repositoryPath!);

            // Assert
            result.Should().BeEmpty();
        }


        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task FetchAsync_WithInvalidPath_ShouldThrowInvalidOperationException(string? repositoryPath)
        {
            // Act & Assert
            var act = async () => await _gitService.FetchAsync(repositoryPath!);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public void IsGitRepository_WithInvalidPath_ShouldReturnFalse(string? path)
        {
            // Act
            var result = _gitService.IsGitRepository(path!);

            // Assert
            result.Should().BeFalse();
        }
    }
}