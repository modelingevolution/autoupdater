using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

/// <summary>
/// Basic infrastructure tests to validate test environment
/// </summary>
public class BasicInfrastructureTests
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<BasicInfrastructureTests> _logger;

    public BasicInfrastructureTests(ITestOutputHelper output)
    {
        _output = output;
        var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new XUnitLoggerProvider(_output)));
        _logger = loggerFactory.CreateLogger<BasicInfrastructureTests>();
    }

    [Fact]
    public async Task Given_DockerIsInstalled_When_ICheckDockerVersion_Then_ShouldReturnVersionInfo()
    {
        // Given: Docker is installed on the system
        _logger.LogInformation("Checking if Docker is available");

        // When: I check Docker version
        var result = await Cli.Wrap("docker")
            .WithArguments(["--version"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

        // Then: Should return version information successfully
        result.ExitCode.Should().Be(0, "Docker should be available and return version info");
        _logger.LogInformation("Docker is available");
    }

    [Fact]
    public async Task Given_DockerComposeIsInstalled_When_ICheckComposeVersion_Then_ShouldReturnVersionInfo()
    {
        // Given: Docker Compose is installed
        _logger.LogInformation("Checking if Docker Compose is available");

        // When: I check Docker Compose version
        var result = await Cli.Wrap("docker")
            .WithArguments(["compose", "version"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

        // Then: Should return version information successfully
        result.ExitCode.Should().Be(0, "Docker Compose should be available and return version info");
        _logger.LogInformation("Docker Compose is available");
    }

    [Fact]
    public async Task Given_HelloWorldImage_When_IRunContainer_Then_ShouldExecuteSuccessfully()
    {
        // Given: hello-world image is available
        _logger.LogInformation("Testing basic Docker container execution");

        // When: I run hello-world container
        var result = await Cli.Wrap("docker")
            .WithArguments(["run", "--rm", "hello-world"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

        // Then: Should execute successfully
        result.ExitCode.Should().Be(0, "hello-world container should run successfully");
        _logger.LogInformation("Docker container execution successful");
    }

    [Fact]
    public async Task Given_SimpleComposeFile_When_IValidateCompose_Then_ShouldParseSuccessfully()
    {
        // Given: A simple Docker Compose file
        var tempDir = Path.GetTempPath();
        var composeFile = Path.Combine(tempDir, $"test-compose-{Guid.NewGuid():N}.yml");
        
        var composeContent = @"
version: '3.8'
services:
  test:
    image: hello-world
";
        await File.WriteAllTextAsync(composeFile, composeContent);
        
        try
        {
            _logger.LogInformation("Validating Docker Compose file: {ComposeFile}", composeFile);

            // When: I validate the compose file
            var result = await Cli.Wrap("docker")
                .WithArguments(["compose", "-f", composeFile, "config"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync();

            // Then: Should validate successfully
            result.ExitCode.Should().Be(0, "Docker Compose file should be valid");
            _logger.LogInformation("Docker Compose file validation successful");
        }
        finally
        {
            // Cleanup
            if (File.Exists(composeFile))
            {
                File.Delete(composeFile);
            }
        }
    }
}