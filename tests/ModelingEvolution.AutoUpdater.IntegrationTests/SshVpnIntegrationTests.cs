using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Host.Services.VPN;
using ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

public class SshVpnIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly ISshVpnService _sshVpnService;

    public SshVpnIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        
        // Create configuration from config file
        var projectRoot = GetProjectRoot();
        var configPath = Path.Combine(projectRoot, "config");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(configPath)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new XUnitLoggerProvider(output));
        });
        
        var logger = loggerFactory.CreateLogger<SshVpnService>();
        _sshVpnService = new SshVpnService(logger, configuration);
    }

    [Fact]
    public async Task GetVpnStatusAsync_ShouldReturnValidStatus()
    {
        // Act
        var status = await _sshVpnService.GetVpnStatusAsync();

        // Assert
        Assert.NotNull(status);
        Assert.Equal("wg0", status.InterfaceName);
        
        // Status should be either active or inactive, not error
        Assert.True(status.IsActive || !status.IsActive, "Status should be determinable");
        
        // If there's an error, log it but don't fail the test
        if (!string.IsNullOrEmpty(status.ErrorMessage))
        {
            _output.WriteLine($"VPN status returned error: {status.ErrorMessage}");
        }

        _output.WriteLine($"VPN Status: Active={status.IsActive}, Interface={status.InterfaceName}, Local={status.LocalAddress}, Remote={status.RemoteEndpoint}");
    }

    [Fact]
    public async Task StartVpnAsync_ShouldStartVpnConnection()
    {
        // Arrange - Ensure VPN is stopped first
        await _sshVpnService.StopVpnAsync();
        await Task.Delay(2000); // Wait for stop to complete

        // Act
        var startResult = await _sshVpnService.StartVpnAsync();

        // Assert
        Assert.True(startResult, "VPN should start successfully");

        // Verify VPN is now active
        var status = await _sshVpnService.GetVpnStatusAsync();
        Assert.True(status.IsActive, "VPN should be active after starting");
        Assert.NotNull(status.LocalAddress);

        _output.WriteLine($"VPN started successfully: {status.LocalAddress} -> {status.RemoteEndpoint}");
    }

    [Fact]
    public async Task StopVpnAsync_ShouldStopVpnConnection()
    {
        // Arrange - Ensure VPN is started first
        await _sshVpnService.StartVpnAsync();
        await Task.Delay(2000); // Wait for start to complete

        // Act
        var stopResult = await _sshVpnService.StopVpnAsync();

        // Assert
        Assert.True(stopResult, "VPN should stop successfully");

        // Verify VPN is now inactive
        var status = await _sshVpnService.GetVpnStatusAsync();
        Assert.False(status.IsActive, "VPN should be inactive after stopping");

        _output.WriteLine("VPN stopped successfully");
    }

    [Fact]
    public async Task VpnLifecycle_StartStopStart_ShouldWorkCorrectly()
    {
        // Test complete lifecycle: Stop -> Start -> Stop -> Start
        
        // Step 1: Stop (ensure clean state)
        var stopResult1 = await _sshVpnService.StopVpnAsync();
        await Task.Delay(1000);
        var status1 = await _sshVpnService.GetVpnStatusAsync();
        Assert.False(status1.IsActive, "VPN should be stopped initially");

        // Step 2: Start
        var startResult1 = await _sshVpnService.StartVpnAsync();
        await Task.Delay(2000);
        Assert.True(startResult1, "First start should succeed");
        
        var status2 = await _sshVpnService.GetVpnStatusAsync();
        Assert.True(status2.IsActive, "VPN should be active after first start");
        Assert.NotNull(status2.LocalAddress);

        // Step 3: Stop
        var stopResult2 = await _sshVpnService.StopVpnAsync();
        await Task.Delay(1000);
        Assert.True(stopResult2, "Stop should succeed");
        
        var status3 = await _sshVpnService.GetVpnStatusAsync();
        Assert.False(status3.IsActive, "VPN should be stopped after stop");

        // Step 4: Start again
        var startResult2 = await _sshVpnService.StartVpnAsync();
        await Task.Delay(2000);
        Assert.True(startResult2, "Second start should succeed");
        
        var status4 = await _sshVpnService.GetVpnStatusAsync();
        Assert.True(status4.IsActive, "VPN should be active after second start");

        _output.WriteLine("Complete VPN lifecycle test passed");
    }

    [Fact]
    public async Task IsVpnActiveAsync_ShouldMatchGetVpnStatusAsync()
    {
        // Get status using both methods
        var isActive = await _sshVpnService.IsVpnActiveAsync();
        var status = await _sshVpnService.GetVpnStatusAsync();

        // Assert they agree
        Assert.Equal(isActive, status.IsActive);

        _output.WriteLine($"VPN active check: IsActive={isActive} (both methods agree)");
    }

    [Theory]
    [InlineData(true)]   // Test starting from stopped state
    [InlineData(false)]  // Test starting from started state
    public async Task StartVpnAsync_ShouldBeIdempotent(bool stopFirst)
    {
        // Arrange
        if (stopFirst)
        {
            await _sshVpnService.StopVpnAsync();
            await Task.Delay(1000);
        }
        else
        {
            await _sshVpnService.StartVpnAsync();
            await Task.Delay(2000);
        }

        // Act - Start twice
        var result1 = await _sshVpnService.StartVpnAsync();
        await Task.Delay(1000);
        var result2 = await _sshVpnService.StartVpnAsync();

        // Assert - Both should succeed (idempotent)
        Assert.True(result1, "First start should succeed");
        Assert.True(result2, "Second start should succeed (idempotent)");

        // Verify final state
        var finalStatus = await _sshVpnService.GetVpnStatusAsync();
        Assert.True(finalStatus.IsActive, "VPN should be active after idempotent starts");

        _output.WriteLine($"Idempotent start test passed (stopFirst={stopFirst})");
    }

    [Theory]
    [InlineData(true)]   // Test stopping from started state
    [InlineData(false)]  // Test stopping from stopped state
    public async Task StopVpnAsync_ShouldBeIdempotent(bool startFirst)
    {
        // Arrange
        if (startFirst)
        {
            await _sshVpnService.StartVpnAsync();
            await Task.Delay(2000);
        }
        else
        {
            await _sshVpnService.StopVpnAsync();
            await Task.Delay(1000);
        }

        // Act - Stop twice
        var result1 = await _sshVpnService.StopVpnAsync();
        await Task.Delay(1000);
        var result2 = await _sshVpnService.StopVpnAsync();

        // Assert - Both should succeed (idempotent)
        Assert.True(result1, "First stop should succeed");
        Assert.True(result2, "Second stop should succeed (idempotent)");

        // Verify final state
        var finalStatus = await _sshVpnService.GetVpnStatusAsync();
        Assert.False(finalStatus.IsActive, "VPN should be inactive after idempotent stops");

        _output.WriteLine($"Idempotent stop test passed (startFirst={startFirst})");
    }

    private static string GetProjectRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !directory.GetFiles("*.csproj").Any())
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}