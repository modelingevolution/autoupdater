using System.Text.Json;
using ModelingEvolution.AutoUpdater.Host.Api.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;

namespace ModelingEvolution.AutoUpdater.Host.Api.AutoUpdater;

public class AutoUpdaterService
{
    private readonly PackageManager _packageManager;
    private readonly ISshConnectionManager _sshManager;
    private readonly IDockerComposeService _dockerComposeService;
    private readonly ILogger<AutoUpdaterService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AutoUpdaterService(
        PackageManager packageManager,
        ISshConnectionManager sshManager,
        IDockerComposeService dockerComposeService,
        ILogger<AutoUpdaterService> logger)
    {
        _packageManager = packageManager;
        _sshManager = sshManager;
        _dockerComposeService = dockerComposeService;
        _logger = logger;
    }

    public async Task<PackagesResponse> GetPackagesAsync()
    {
        // Get both data sources concurrently
        var composeStatusMap = await _dockerComposeService.GetDockerComposeStatusAsync();
        var packageInfos = await _packageManager.GetPackagesAsync();
        
        
        
        var packages = packageInfos.Select(packageInfo => new PackageStatus
        {
            Name = packageInfo.Name,
            RepositoryUrl = packageInfo.RepositoryUrl,
            CurrentVersion = packageInfo.CurrentVersion,
            LastChecked = packageInfo.LastChecked,
            Status = GetPackageStatus(packageInfo.Name, composeStatusMap)
        }).ToList();

        return new PackagesResponse { Packages = packages };
    }


    private string GetPackageStatus(PackageName packageName, Dictionary<PackageName, ComposeProjectStatus> composeStatusMap)
    {
        // Try exact match first
        if (composeStatusMap.TryGetValue(packageName, out var exactMatch))
        {
            return MapComposeStatusToPackageStatus(exactMatch.Status);
        }

        // Try partial match by name similarity
        var partialMatch = composeStatusMap.FirstOrDefault(kvp => 
            string.Equals(kvp.Key.ToString(), packageName.ToString(), StringComparison.OrdinalIgnoreCase));

        if (partialMatch.Key != default)
        {
            _logger.LogDebug("Found partial match for package {PackageName}: {ComposeName}", 
                packageName, partialMatch.Key);
            return MapComposeStatusToPackageStatus(partialMatch.Value.Status);
        }

        // No match found - package might not be deployed or not running
        _logger.LogDebug("No Docker Compose project found for package {PackageName}", packageName);
        return "not-deployed";
    }

    private static string MapComposeStatusToPackageStatus(string composeStatus) =>
        composeStatus switch
        {
            var s when s.Contains("running", StringComparison.OrdinalIgnoreCase) => "running",
            var s when s.Contains("exited", StringComparison.OrdinalIgnoreCase) => "stopped",
            var s when s.Contains("paused", StringComparison.OrdinalIgnoreCase) => "paused",
            var s when s.Contains("restarting", StringComparison.OrdinalIgnoreCase) => "restarting",
            var s when s.Contains("dead", StringComparison.OrdinalIgnoreCase) => "failed",
            _ => "unknown"
        };

    public async Task<UpgradeStatusResponse> GetUpgradeStatusAsync(PackageName packageName)
    {
        var packageInfo = await _packageManager.GetPackageAsync(packageName);

        if (packageInfo == null)
        {
            throw new PackageNotFoundException($"Package '{packageName}' not found");
        }

        return new UpgradeStatusResponse
        {
            PackageName = packageInfo.Name,
            CurrentVersion = packageInfo.CurrentVersion,
            AvailableVersion = packageInfo.LatestVersion,
            UpgradeAvailable = packageInfo.UpgradeAvailable,
            Changelog = "Bug fixes and performance improvements" // Placeholder
        };
    }

    public Task<UpdateResponse> TriggerUpdateAsync(PackageName packageName)
    {
        var updateId = Guid.NewGuid().ToString();
        _ = StartBackgroundUpdate(packageName, updateId);

        return Task.FromResult(new UpdateResponse
        {
            PackageName = packageName,
            UpdateId = updateId,
            Status = "started",
            Message = "Update process initiated"
        });
    }

    public async Task<UpdateAllResponse> TriggerUpdateAllAsync()
    {
        var packageInfos = await _packageManager.GetPackagesAsync();
        var result = ProcessPackagesForUpdate(packageInfos);

        // If no packages to update, trigger the update process anyway
        if (result.UpdatesStarted.Count == 0)
        {
            _ = Task.Run(_packageManager.UpdateAllAsync);
        }

        return new UpdateAllResponse
        {
            UpdatesStarted = result.UpdatesStarted,
            Skipped = result.Skipped
        };
    }
    
    private UpdateProcessResult ProcessPackagesForUpdate(IEnumerable<PackageInfo> packageInfos)
    {
        var updatesStarted = new List<UpdateInfo>();
        var skipped = new List<SkippedPackage>();
        
        foreach (var packageInfo in packageInfos)
        {
            try
            {
                if (packageInfo.UpgradeAvailable)
                {
                    var updateId = Guid.NewGuid().ToString();
                    _ = StartBackgroundUpdate(packageInfo.Name, updateId);

                    updatesStarted.Add(new UpdateInfo
                    {
                        PackageName = packageInfo.Name,
                        UpdateId = updateId,
                        FromVersion = packageInfo.CurrentVersion,
                        ToVersion = packageInfo.LatestVersion
                    });
                }
                else
                {
                    skipped.Add(new SkippedPackage
                    {
                        PackageName = packageInfo.Name,
                        Reason = "No update available"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing package {PackageName}", packageInfo.Name);
                skipped.Add(new SkippedPackage
                {
                    PackageName = packageInfo.Name,
                    Reason = $"Error: {ex.Message}"
                });
            }
        }
        
        return new UpdateProcessResult
        {
            UpdatesStarted = updatesStarted,
            Skipped = skipped
        };
    }
    
    private Task StartBackgroundUpdate(PackageName packageName, string updateId) =>
        Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Starting update for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
                await _packageManager.TriggerUpdateAsync(packageName);
                _logger.LogInformation("Update completed for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update failed for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
            }
        });
}

public class PackageNotFoundException : Exception
{
    public PackageNotFoundException(string message) : base(message) { }
}