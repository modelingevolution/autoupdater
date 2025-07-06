using FluentAssertions;
using ModelingEvolution.AutoUpdater;

namespace ModelingEvolution.AutoUpdater.Tests;

public class GitTagVersionTests
{
    [Theory]
    [InlineData("v1.2.3", true, "1.2.3")]
    [InlineData("ver1.0.0", true, "1.0.0")]
    [InlineData("2.1.0", true, "2.1.0")]
    [InlineData("invalid", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void TryParse_ShouldParseVersionCorrectly(string? input, bool expectedResult, string? expectedVersion)
    {
        // Act
        var result = GitTagVersion.TryParse(input, out var gitTagVersion);

        // Assert
        result.Should().Be(expectedResult);
        
        if (expectedResult)
        {
            gitTagVersion.Should().NotBeNull();
            gitTagVersion!.Version.ToString().Should().Be(expectedVersion);
            gitTagVersion.FriendlyName.Should().Be(input);
        }
        else
        {
            gitTagVersion.Should().BeNull();
        }
    }

    [Fact]
    public void CompareTo_ShouldCompareVersionsCorrectly()
    {
        // Arrange
        var version1 = new GitTagVersion("v1.0.0", new Version(1, 0, 0));
        var version2 = new GitTagVersion("v1.2.0", new Version(1, 2, 0));
        var version3 = new GitTagVersion("v2.0.0", new Version(2, 0, 0));

        // Act & Assert
        version1.CompareTo(version2).Should().BeLessThan(0);
        version2.CompareTo(version1).Should().BeGreaterThan(0);
        version2.CompareTo(version3).Should().BeLessThan(0);
        version1.CompareTo(version1).Should().Be(0);
        version1.CompareTo(null).Should().BeGreaterThan(0);
    }

    [Fact]
    public void ImplicitOperator_ShouldConvertToString()
    {
        // Arrange
        var version = new GitTagVersion("v1.2.3", new Version(1, 2, 3));

        // Act
        string result = version;

        // Assert
        result.Should().Be("v1.2.3");
    }

    [Fact]
    public void ToString_ShouldReturnFriendlyName()
    {
        // Arrange
        var version = new GitTagVersion("v1.2.3", new Version(1, 2, 3));

        // Act
        var result = version.ToString();

        // Assert
        result.Should().Be("v1.2.3");
    }
}