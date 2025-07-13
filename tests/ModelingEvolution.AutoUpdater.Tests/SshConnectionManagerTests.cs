using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater;
using NSubstitute;

namespace ModelingEvolution.AutoUpdater.Tests;

public class SshConnectionManagerTests
{
    private readonly ILogger<SshConnectionManager> _logger = Substitute.For<ILogger<SshConnectionManager>>();

    [Fact]
    public void Constructor_WithNullConfig_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var act = () => new SshConnectionManager(null!, _logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("config");
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
    {
        // Arrange
        var config = new SshConfiguration();

        // Act & Assert
        var act = () => new SshConnectionManager(config, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_WithValidParameters_ShouldNotThrow()
    {
        // Arrange
        var config = new SshConfiguration
        {
            Host = "test-host",
            User = "test-user",
            Password = "test-password"
        };

        // Act & Assert
        var act = () => new SshConnectionManager(config, _logger);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task CreateConnectionAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var config = new SshConfiguration
        {
            Host = "test-host",
            User = "test-user",
            Password = "test-password"
        };
        var manager = new SshConnectionManager(config, _logger);
        manager.Dispose();

        // Act & Assert
        var act = async () => await manager.CreateConnectionAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Theory]
    [InlineData(SshAuthMethod.Password, "", "Password is required for password authentication")]
    [InlineData(SshAuthMethod.PrivateKey, "", "KeyPath is required for private key authentication")]
    [InlineData(SshAuthMethod.PrivateKeyWithPassphrase, "", "KeyPath is required for private key authentication")]
    public async Task CreateConnectionAsync_WithInvalidConfiguration_ShouldThrowInvalidOperationException(
        SshAuthMethod authMethod, string keyPath, string expectedMessage)
    {
        // Arrange
        var config = new SshConfiguration
        {
            Host = "test-host",
            User = "test-user",
            AuthMethod = authMethod,
            KeyPath = keyPath
        };
        var manager = new SshConnectionManager(config, _logger);

        // Act & Assert
        var act = async () => await manager.CreateConnectionAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(expectedMessage);
    }

    [Fact]
    public async Task CreateConnectionAsync_WithPassphraseAuthButNoPassphrase_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var config = new SshConfiguration
        {
            Host = "test-host",
            User = "test-user",
            AuthMethod = SshAuthMethod.PrivateKeyWithPassphrase,
            KeyPath = "/path/to/key"
        };
        var manager = new SshConnectionManager(config, _logger);

        // Act & Assert
        var act = async () => await manager.CreateConnectionAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Passphrase is required for passphrase-protected private key authentication");
    }

    [Fact]
    public void SshCommandResult_IsSuccess_ShouldReturnTrueForExitCodeZero()
    {
        // Arrange
        var result = new SshCommandResult
        {
            ExitCode = 0
        };

        // Act & Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(127)]
    public void SshCommandResult_IsSuccess_ShouldReturnFalseForNonZeroExitCode(int exitCode)
    {
        // Arrange
        var result = new SshCommandResult
        {
            ExitCode = exitCode
        };

        // Act & Assert
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void SshCommandResult_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var result = new SshCommandResult();

        // Assert
        result.Command.Should().Be(string.Empty);
        result.ExitCode.Should().Be(0);
        result.Output.Should().Be(string.Empty);
        result.Error.Should().Be(string.Empty);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithoutConnection_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var config = new SshConfiguration
        {
            Host = "test-host",
            User = "test-user",
            Password = "test-password"
        };
        var manager = new SshConnectionManager(config, _logger);

        // Act & Assert
        var act = async () => await manager.ExecuteCommandAsync("echo test");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SSH client is not connected. Call CreateConnectionAsync first.");
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Arrange
        var config = new SshConfiguration();
        var manager = new SshConnectionManager(config, _logger);

        // Act & Assert
        var act = () => manager.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_ShouldNotThrow()
    {
        // Arrange
        var config = new SshConfiguration();
        var manager = new SshConnectionManager(config, _logger);

        // Act & Assert
        var act = () =>
        {
            manager.Dispose();
            manager.Dispose();
            manager.Dispose();
        };
        act.Should().NotThrow();
    }
}