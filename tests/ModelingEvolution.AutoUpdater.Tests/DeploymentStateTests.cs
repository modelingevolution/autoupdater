using FluentAssertions;
using ModelingEvolution.AutoUpdater;

namespace ModelingEvolution.AutoUpdater.Tests;

public class DeploymentStateTests
{
    [Fact]
    public void Constructor_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var version = "v1.2.3";
        var updated = DateTime.Now;

        // Act
        var deploymentState = new DeploymentState(version, updated);

        // Assert
        deploymentState.Version.Should().Be(version);
        deploymentState.Updated.Should().Be(updated);
    }

    [Fact]
    public void Properties_ShouldBeInitializedCorrectly()
    {
        // Arrange & Act
        var deploymentState = new DeploymentState("v2.0.0", new DateTime(2024, 1, 1));

        // Assert
        deploymentState.Version.Should().Be("v2.0.0");
        deploymentState.Updated.Should().Be(new DateTime(2024, 1, 1));
    }
}
