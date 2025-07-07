using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;

/// <summary>
/// Client for interacting with AutoUpdater API endpoints
/// </summary>
public class AutoUpdaterClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AutoUpdaterClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public AutoUpdaterClient(string baseUrl, ILogger<AutoUpdaterClient> logger)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>
    /// Waits for AutoUpdater to be available
    /// </summary>
    public async Task WaitForAvailabilityAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Waiting for AutoUpdater to be available at {BaseUrl} (timeout: {Timeout})", 
            _httpClient.BaseAddress, timeout);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var startTime = DateTime.UtcNow;

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var response = await _httpClient.GetAsync("/api/packages", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    _logger.LogInformation("AutoUpdater is available after {Elapsed}", elapsed);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Expected while service is starting up
            }

            await Task.Delay(1000, cts.Token);
        }

        throw new TimeoutException($"AutoUpdater did not become available within {timeout}");
    }

    /// <summary>
    /// Gets all configured packages
    /// </summary>
    public async Task<PackagesResponse> GetPackagesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting packages from AutoUpdater");

        var response = await _httpClient.GetAsync("/api/packages", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<PackagesResponse>(content, _jsonOptions);
        
        return result ?? new PackagesResponse();
    }

    /// <summary>
    /// Gets upgrade status for a specific package
    /// </summary>
    public async Task<UpgradeStatusResponse> GetUpgradeStatusAsync(string packageName, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting upgrade status for package: {PackageName}", packageName);

        var response = await _httpClient.GetAsync($"/api/upgrades/{packageName}", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<UpgradeStatusResponse>(content, _jsonOptions);
        
        return result ?? new UpgradeStatusResponse();
    }

    /// <summary>
    /// Triggers an update for a specific package
    /// </summary>
    public async Task<UpdateResponse> TriggerUpdateAsync(string packageName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Triggering update for package: {PackageName}", packageName);

        var response = await _httpClient.PostAsync($"/api/update/{packageName}", null, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<UpdateResponse>(content, _jsonOptions);
        
        return result ?? new UpdateResponse();
    }

    /// <summary>
    /// Triggers update for all packages
    /// </summary>
    public async Task<UpdateAllResponse> TriggerUpdateAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Triggering update for all packages");

        var response = await _httpClient.PostAsync("/api/update-all", null, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<UpdateAllResponse>(content, _jsonOptions);
        
        return result ?? new UpdateAllResponse();
    }

    /// <summary>
    /// Waits for a package to have an upgrade available
    /// </summary>
    public async Task WaitForUpgradeAvailableAsync(string packageName, string expectedVersion, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Waiting for upgrade to {ExpectedVersion} to be available for package {PackageName} (timeout: {Timeout})", 
            expectedVersion, packageName, timeout);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var startTime = DateTime.UtcNow;

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var status = await GetUpgradeStatusAsync(packageName, cts.Token);
                if (status.UpgradeAvailable && status.AvailableVersion == expectedVersion)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    _logger.LogInformation("Upgrade to {ExpectedVersion} is available for {PackageName} after {Elapsed}", 
                        expectedVersion, packageName, elapsed);
                    return;
                }

                _logger.LogDebug("Current status for {PackageName}: Available={AvailableVersion}, UpgradeAvailable={UpgradeAvailable}", 
                    packageName, status.AvailableVersion, status.UpgradeAvailable);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking upgrade status for {PackageName}", packageName);
            }

            await Task.Delay(5000, cts.Token); // Check every 5 seconds
        }

        throw new TimeoutException($"Upgrade to {expectedVersion} for package {packageName} was not available within {timeout}");
    }

    /// <summary>
    /// Waits for an update to complete
    /// </summary>
    public async Task WaitForUpdateCompletionAsync(string packageName, string expectedVersion, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Waiting for update to {ExpectedVersion} to complete for package {PackageName} (timeout: {Timeout})", 
            expectedVersion, packageName, timeout);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var startTime = DateTime.UtcNow;

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var status = await GetUpgradeStatusAsync(packageName, cts.Token);
                if (status.CurrentVersion == expectedVersion && !status.UpgradeAvailable)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    _logger.LogInformation("Update to {ExpectedVersion} completed for {PackageName} after {Elapsed}", 
                        expectedVersion, packageName, elapsed);
                    return;
                }

                _logger.LogDebug("Update progress for {PackageName}: Current={CurrentVersion}, Target={ExpectedVersion}", 
                    packageName, status.CurrentVersion, expectedVersion);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking update progress for {PackageName}", packageName);
            }

            await Task.Delay(5000, cts.Token); // Check every 5 seconds
        }

        throw new TimeoutException($"Update to {expectedVersion} for package {packageName} did not complete within {timeout}");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

// Response DTOs
public class PackagesResponse
{
    public List<PackageInfo> Packages { get; set; } = new();
}

public class PackageInfo
{
    public string Name { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public DateTime LastChecked { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class UpgradeStatusResponse
{
    public string PackageName { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string AvailableVersion { get; set; } = string.Empty;
    public bool UpgradeAvailable { get; set; }
    public string Changelog { get; set; } = string.Empty;
}

public class UpdateResponse
{
    public string PackageName { get; set; } = string.Empty;
    public string UpdateId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class UpdateAllResponse
{
    public List<UpdateInfo> UpdatesStarted { get; set; } = new();
    public List<SkippedPackage> Skipped { get; set; } = new();
}

public class UpdateInfo
{
    public string PackageName { get; set; } = string.Empty;
    public string UpdateId { get; set; } = string.Empty;
    public string FromVersion { get; set; } = string.Empty;
    public string ToVersion { get; set; } = string.Empty;
}

public class SkippedPackage
{
    public string PackageName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}