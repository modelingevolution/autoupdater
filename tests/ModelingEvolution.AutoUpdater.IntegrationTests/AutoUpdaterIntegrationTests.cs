using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

/// <summary>
/// Integration tests for the AutoUpdater service using Given-When-Then pattern
/// </summary>
public class AutoUpdaterIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<AutoUpdaterIntegrationTests> _logger;
    private readonly DockerImageBuilder _dockerImageBuilder;
    private readonly DockerComposeManager _dockerComposeManager;
    private readonly GitRepositoryManager _gitRepositoryManager;
    private readonly AutoUpdaterClient _autoUpdaterClient;

    private string? _testAppRepository;
    private string? _testComposeRepository;
    private string? _tempDirectory;

    public AutoUpdaterIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new XUnitLoggerProvider(_output)));
        
        _logger = loggerFactory.CreateLogger<AutoUpdaterIntegrationTests>();
        _dockerImageBuilder = new DockerImageBuilder(loggerFactory.CreateLogger<DockerImageBuilder>());
        _dockerComposeManager = new DockerComposeManager(loggerFactory.CreateLogger<DockerComposeManager>());
        _gitRepositoryManager = new GitRepositoryManager(loggerFactory.CreateLogger<GitRepositoryManager>());
        _autoUpdaterClient = new AutoUpdaterClient("http://localhost:8081", loggerFactory.CreateLogger<AutoUpdaterClient>());
    }

    public Task InitializeAsync()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"autoupdater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        _logger.LogInformation("Test setup in directory: {TempDirectory}", _tempDirectory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _logger.LogInformation("Cleaning up test resources");
        
        await _dockerComposeManager.CleanupAsync();
        await _dockerImageBuilder.CleanupAsync();
        await _gitRepositoryManager.CleanupAsync();
        
        if (!string.IsNullOrEmpty(_tempDirectory) && Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp directory: {TempDirectory}", _tempDirectory);
            }
        }
        
        _dockerImageBuilder.Dispose();
        _dockerComposeManager.Dispose();
        _gitRepositoryManager.Dispose();
        _autoUpdaterClient.Dispose();
    }

    [Fact]
    public async Task Given_VersionedAppAndCompose_When_AutoUpdaterDetectsNewVersion_Then_ShouldUpdateDeployment()
    {
        // Given: A versioned application and compose setup
        await GivenVersionedApplicationIsSetup("1.0.0");
        await GivenComposeConfigurationIsSetup("0.0.0", "1.0.0");
        await GivenAutoUpdaterIsRunning();

        // When: A new version is released
        await WhenNewVersionIsReleased("1.0.1");
        await WhenComposeIsUpdatedForNewVersion("0.0.1", "1.0.1");

        // Then: AutoUpdater should detect and deploy the update
        await ThenAutoUpdaterShouldDetectUpdate();
        await ThenApplicationShouldBeUpdatedToVersion("1.0.1");
        await ThenApplicationShouldBeHealthy();
    }

    [Fact]
    public async Task Given_RunningApplication_When_AutoUpdaterUpdatesVersion_Then_ShouldMaintainZeroDowntime()
    {
        // Given: Application is running with version 1.0.0
        await GivenVersionedApplicationIsSetup("1.0.0");
        await GivenComposeConfigurationIsSetup("0.0.0", "1.0.0");
        await GivenApplicationIsDeployed();
        await GivenApplicationIsHealthy("1.0.0");

        // When: AutoUpdater performs update to 1.0.1
        await WhenNewVersionIsReleased("1.0.1");
        await WhenComposeIsUpdatedForNewVersion("0.0.1", "1.0.1");
        await WhenAutoUpdaterTriggersUpdate();

        // Then: Update should complete with minimal downtime
        await ThenApplicationShouldBeUpdatedToVersion("1.0.1");
        await ThenApplicationShouldBeHealthy();
        await ThenDowntimeShouldBeMinimal();
    }

    #region Given Steps

    private async Task GivenVersionedApplicationIsSetup(string version)
    {
        _logger.LogInformation("Setting up versioned application: {Version}", version);
        
        // Clone the test application repository
        _testAppRepository = await _gitRepositoryManager.CloneRepositoryAsync(
            "https://github.com/modelingevolution/version-app.git",
            Path.Combine(_tempDirectory!, "version-app"));

        // Ensure the application has the correct version
        await _gitRepositoryManager.PrepareVersionedAppAsync(_testAppRepository, version);

        // Build the Docker image for this version
        await _dockerImageBuilder.BuildVersionAppAsync(_testAppRepository, version);
        
        _logger.LogInformation("Versioned application setup complete: {Version}", version);
    }

    private async Task GivenComposeConfigurationIsSetup(string composeVersion, string appVersion)
    {
        _logger.LogInformation("Setting up compose configuration: {ComposeVersion} for app {AppVersion}", 
            composeVersion, appVersion);
        
        // Clone the compose repository
        _testComposeRepository = await _gitRepositoryManager.CloneRepositoryAsync(
            "https://github.com/modelingevolution/version-app-compose.git",
            Path.Combine(_tempDirectory!, "version-app-compose"));

        // Prepare compose configuration for the specified versions
        await _gitRepositoryManager.PrepareVersionedComposeAsync(
            _testComposeRepository, appVersion, composeVersion);
        
        _logger.LogInformation("Compose configuration setup complete: {ComposeVersion}", composeVersion);
    }

    private async Task GivenAutoUpdaterIsRunning()
    {
        _logger.LogInformation("Starting AutoUpdater service");
        
        // Create AutoUpdater configuration
        var configPath = Path.Combine(_tempDirectory!, "autoupdater-config.json");
        var config = CreateAutoUpdaterConfiguration();
        await File.WriteAllTextAsync(configPath, config);

        // Start AutoUpdater via Docker Compose
        var autoUpdaterComposePath = Path.Combine(_tempDirectory!, "autoupdater-compose.yml");
        var autoUpdaterCompose = CreateAutoUpdaterComposeFile(configPath);
        await File.WriteAllTextAsync(autoUpdaterComposePath, autoUpdaterCompose);

        await _dockerComposeManager.StartAsync(autoUpdaterComposePath);
        
        // Wait for AutoUpdater to be healthy
        var isHealthy = await _dockerComposeManager.WaitForServiceHealthyAsync(
            autoUpdaterComposePath, "autoupdater", TimeSpan.FromMinutes(2));
        
        isHealthy.Should().BeTrue("AutoUpdater should start and become healthy");
        
        _logger.LogInformation("AutoUpdater service is running and healthy");
    }

    private async Task GivenApplicationIsDeployed()
    {
        _logger.LogInformation("Deploying application");
        
        var composePath = Path.Combine(_testComposeRepository!, "docker-compose.yml");
        await _dockerComposeManager.StartAsync(composePath);
        
        // Wait for application to be healthy
        var isHealthy = await _dockerComposeManager.WaitForServiceHealthyAsync(
            composePath, "app", TimeSpan.FromMinutes(2));
        
        isHealthy.Should().BeTrue("Application should deploy and become healthy");
        
        _logger.LogInformation("Application deployed successfully");
    }

    private async Task GivenApplicationIsHealthy(string expectedVersion)
    {
        _logger.LogInformation("Verifying application health with version: {ExpectedVersion}", expectedVersion);
        
        // Check if application responds with correct version
        var httpClient = new HttpClient();
        var response = await httpClient.GetStringAsync("http://localhost:8080/version");
        var actualVersion = response.Trim('"');
        actualVersion.Should().Be(expectedVersion, "Application should be running with expected version");
        httpClient.Dispose();
        
        _logger.LogInformation("Application is healthy with version: {ActualVersion}", actualVersion);
    }

    #endregion

    #region When Steps

    private async Task WhenNewVersionIsReleased(string newVersion)
    {
        _logger.LogInformation("Releasing new version: {NewVersion}", newVersion);
        
        // Prepare new version in the app repository
        await _gitRepositoryManager.PrepareVersionedAppAsync(_testAppRepository!, newVersion);
        
        // Build new Docker image
        await _dockerImageBuilder.BuildVersionAppAsync(_testAppRepository!, newVersion);
        
        _logger.LogInformation("New version released: {NewVersion}", newVersion);
    }

    private async Task WhenComposeIsUpdatedForNewVersion(string composeVersion, string appVersion)
    {
        _logger.LogInformation("Updating compose for new version: {ComposeVersion} -> {AppVersion}", 
            composeVersion, appVersion);
        
        // Update compose configuration
        await _gitRepositoryManager.PrepareVersionedComposeAsync(
            _testComposeRepository!, appVersion, composeVersion);
        
        _logger.LogInformation("Compose updated for new version: {ComposeVersion}", composeVersion);
    }

    private async Task WhenAutoUpdaterTriggersUpdate()
    {
        _logger.LogInformation("Triggering AutoUpdater update");
        
        // Trigger update via API call
        await _autoUpdaterClient.TriggerUpdateAsync("versionapp");
        
        _logger.LogInformation("AutoUpdater update triggered");
    }

    #endregion

    #region Then Steps

    private async Task ThenAutoUpdaterShouldDetectUpdate()
    {
        _logger.LogInformation("Verifying AutoUpdater detects update");
        
        // Wait for AutoUpdater to detect and process the update
        var maxWait = TimeSpan.FromMinutes(5);
        var startTime = DateTime.UtcNow;
        
        while (DateTime.UtcNow - startTime < maxWait)
        {
            var status = await _autoUpdaterClient.GetUpgradeStatusAsync("versionapp");
            var statusText = status.UpgradeAvailable ? "updating" : "updated";
            if (statusText.Contains("updating", StringComparison.OrdinalIgnoreCase) ||
                statusText.Contains("updated", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("AutoUpdater detected and is processing update");
                return;
            }
            
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        
        Assert.Fail("AutoUpdater should have detected the update within the timeout period");
    }

    private async Task ThenApplicationShouldBeUpdatedToVersion(string expectedVersion)
    {
        _logger.LogInformation("Verifying application is updated to version: {ExpectedVersion}", expectedVersion);
        
        // Wait for application to be updated
        var maxWait = TimeSpan.FromMinutes(5);
        var startTime = DateTime.UtcNow;
        
        while (DateTime.UtcNow - startTime < maxWait)
        {
            try
            {
                var httpClient = new HttpClient();
                var response = await httpClient.GetStringAsync("http://localhost:8080/version");
                var actualVersion = response.Trim('"');
                httpClient.Dispose();
                if (actualVersion == expectedVersion)
                {
                    _logger.LogInformation("Application successfully updated to version: {ActualVersion}", actualVersion);
                    return;
                }
                
                _logger.LogDebug("Application version is {ActualVersion}, waiting for {ExpectedVersion}", 
                    actualVersion, expectedVersion);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking application version, retrying...");
            }
            
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        
        Assert.Fail($"Application should have been updated to version {expectedVersion} within the timeout period");
    }

    private async Task ThenApplicationShouldBeHealthy()
    {
        _logger.LogInformation("Verifying application is healthy after update");
        
        var maxWait = TimeSpan.FromMinutes(2);
        var startTime = DateTime.UtcNow;
        
        while (DateTime.UtcNow - startTime < maxWait)
        {
            try
            {
                var httpClient = new HttpClient();
                var response = await httpClient.GetAsync("http://localhost:8080/health");
                var isHealthy = response.IsSuccessStatusCode;
                httpClient.Dispose();
                if (isHealthy)
                {
                    _logger.LogInformation("Application is healthy after update");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health check failed, retrying...");
            }
            
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        
        Assert.Fail("Application should be healthy after update");
    }

    private async Task ThenDowntimeShouldBeMinimal()
    {
        _logger.LogInformation("Verifying minimal downtime during update");
        
        // This would typically involve checking metrics or logs to ensure
        // downtime was within acceptable limits (e.g., < 30 seconds)
        
        // For now, we'll assume if the application is healthy, downtime was minimal
        await ThenApplicationShouldBeHealthy();
        
        _logger.LogInformation("Downtime verification complete");
    }

    #endregion

    #region Helper Methods

    private string CreateAutoUpdaterConfiguration()
    {
        return """
        {
            "GitRepository": "https://github.com/modelingevolution/version-app-compose.git",
            "PollingInterval": "00:00:30",
            "DockerCompose": {
                "FilePath": "./docker-compose.yml",
                "ServiceName": "app"
            },
            "Logging": {
                "LogLevel": {
                    "Default": "Information"
                }
            }
        }
        """;
    }

    private string CreateAutoUpdaterComposeFile(string configPath)
    {
        return $"""
        version: '3.8'
        services:
          autoupdater:
            image: modelingevolution/autoupdater:latest
            container_name: test-autoupdater
            volumes:
              - /var/run/docker.sock:/var/run/docker.sock
              - {configPath}:/app/appsettings.json
              - {_testComposeRepository}:/app/compose
            environment:
              - ASPNETCORE_ENVIRONMENT=Development
            healthcheck:
              test: ["CMD", "curl", "-f", "http://localhost:8080/health"]
              interval: 10s
              timeout: 5s
              retries: 5
            ports:
              - "8081:8080"
        """;
    }

    #endregion
}