using FluentAssertions;
using ModelingEvolution.AutoUpdater;

namespace ModelingEvolution.AutoUpdater.Tests;

public class SshConfigurationTests
{
    [Fact]
    public void SshConfiguration_GetSafeConfigurationSummary_ShouldNotExposeSensitiveData()
    {
        // Arrange
        var config = new SshConfiguration
        {
            Host = "test-host",
            User = "test-user",
            Password = "secret-password",
            KeyPath = "/path/to/key",
            Passphrase = "secret-passphrase",
            AuthMethod = SshAuthMethod.KeyWithPasswordFallback,
            Port = 2222,
            Timeout = TimeSpan.FromSeconds(60)
        };

        // Act
        var summary = config.GetSafeConfigurationSummary();

        // Assert
        summary.Should().Contain("Host: test-host");
        summary.Should().Contain("User: test-user");
        summary.Should().Contain("Port: 2222");
        summary.Should().Contain("AuthMethod: KeyWithPasswordFallback");
        summary.Should().Contain("HasPassword: True");
        summary.Should().Contain("HasKeyPath: True");
        summary.Should().Contain("HasPassphrase: True");
        summary.Should().Contain("Timeout: 60s");
        
        // Should NOT contain sensitive data
        summary.Should().NotContain("secret-password");
        summary.Should().NotContain("secret-passphrase");
        summary.Should().NotContain("/path/to/key");
    }

    [Fact]
    public void GlobalSshConfiguration_ToSshConfiguration_ShouldMapPropertiesCorrectly()
    {
        // Arrange
        var globalConfig = new GlobalSshConfiguration
        {
            SshUser = "deploy",
            SshPwd = "password123",
            SshKeyPath = "/data/ssh/id_rsa",
            SshKeyPassphrase = "passphrase123",
            SshAuthMethod = SshAuthMethod.PrivateKeyWithPassphrase,
            SshPort = 2222,
            SshTimeoutSeconds = 45,
            SshEnableCompression = false,
            SshKeepAliveSeconds = 20
        };

        // Act
        var sshConfig = globalConfig.ToSshConfiguration("test-host");

        // Assert
        sshConfig.Host.Should().Be("test-host");
        sshConfig.User.Should().Be("deploy");
        sshConfig.Password.Should().Be("password123");
        sshConfig.KeyPath.Should().Be("/data/ssh/id_rsa");
        sshConfig.Passphrase.Should().Be("passphrase123");
        sshConfig.AuthMethod.Should().Be(SshAuthMethod.PrivateKeyWithPassphrase);
        sshConfig.Port.Should().Be(2222);
        sshConfig.Timeout.Should().Be(TimeSpan.FromSeconds(45));
        sshConfig.EnableCompression.Should().BeFalse();
        sshConfig.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void GlobalSshConfiguration_GetSafeConfigurationSummary_ShouldNotExposeSensitiveData()
    {
        // Arrange
        var config = new GlobalSshConfiguration
        {
            SshUser = "admin",
            SshPwd = "admin123",
            SshKeyPath = "/secret/path/key",
            SshKeyPassphrase = "secret123",
            SshAuthMethod = SshAuthMethod.Password
        };

        // Act
        var summary = config.GetSafeConfigurationSummary();

        // Assert
        summary.Should().Contain("User: admin");
        summary.Should().Contain("AuthMethod: Password");
        summary.Should().Contain("HasPassword: True");
        summary.Should().Contain("HasKeyPath: True");
        summary.Should().Contain("HasPassphrase: True");
        
        // Should NOT contain sensitive data
        summary.Should().NotContain("admin123");
        summary.Should().NotContain("secret123");
        summary.Should().NotContain("/secret/path/key");
    }

    [Theory]
    [InlineData(null, "not set")]
    [InlineData("", "not set")]
    [InlineData("testuser", "testuser")]
    public void GlobalSshConfiguration_GetSafeConfigurationSummary_ShouldHandleNullUser(string? user, string expectedUserDisplay)
    {
        // Arrange
        var config = new GlobalSshConfiguration
        {
            SshUser = user
        };

        // Act
        var summary = config.GetSafeConfigurationSummary();

        // Assert
        summary.Should().Contain($"User: {expectedUserDisplay}");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("password", true)]
    public void GlobalSshConfiguration_GetSafeConfigurationSummary_ShouldIndicatePasswordPresence(string? password, bool hasPassword)
    {
        // Arrange
        var config = new GlobalSshConfiguration
        {
            SshPwd = password
        };

        // Act
        var summary = config.GetSafeConfigurationSummary();

        // Assert
        summary.Should().Contain($"HasPassword: {hasPassword}");
    }

    [Fact]
    public void SshConfiguration_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var config = new SshConfiguration();

        // Assert
        config.Host.Should().Be(string.Empty);
        config.User.Should().Be(string.Empty);
        config.Password.Should().BeNull();
        config.KeyPath.Should().BeNull();
        config.Passphrase.Should().BeNull();
        config.AuthMethod.Should().Be(SshAuthMethod.Password);
        config.Port.Should().Be(22);
        config.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        config.EnableCompression.Should().BeTrue();
        config.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void GlobalSshConfiguration_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var config = new GlobalSshConfiguration();

        // Assert
        config.SshUser.Should().BeNull();
        config.SshPwd.Should().BeNull();
        config.SshKeyPath.Should().BeNull();
        config.SshKeyPassphrase.Should().BeNull();
        config.SshAuthMethod.Should().Be(SshAuthMethod.Password);
        config.SshPort.Should().Be(22);
        config.SshTimeoutSeconds.Should().Be(30);
        config.SshEnableCompression.Should().BeTrue();
        config.SshKeepAliveSeconds.Should().Be(30);
    }
}