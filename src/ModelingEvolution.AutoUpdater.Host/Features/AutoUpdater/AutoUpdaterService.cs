using ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater.Models;
using System.Text.Json;
using ModelingEvolution.AutoUpdater.Extensions;

namespace ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater;

public class AutoUpdaterService
{
    private readonly UpdateService _updateService;
    private readonly SshConnectionManager _sshManager;
    private readonly ILogger<AutoUpdaterService> _logger;

    public AutoUpdaterService(
        UpdateService updateService,
        SshConnectionManager sshManager,
        ILogger<AutoUpdaterService> logger)
    {
        _updateService = updateService;
        _sshManager = sshManager;
        _logger = logger;
    }

    public async Task<PackagesResponse> GetPackagesAsync()
    {
        var packages = new List<PackageStatus>();
        
        // Get Docker Compose status via SSH
        var composeStatusMap = await GetDockerComposeStatusAsync();
        
        // Get packages from UpdateService
        var packageInfos = await _updateService.GetPackagesAsync();
        
        foreach (var packageInfo in packageInfos)
        {
            // Determine status from Docker Compose status
            var status = GetPackageStatus(packageInfo.Name, composeStatusMap);
            
            packages.Add(new PackageStatus
            {
                Name = packageInfo.Name,
                RepositoryUrl = packageInfo.RepositoryUrl,
                CurrentVersion = packageInfo.CurrentVersion,
                LastChecked = packageInfo.LastChecked,
                Status = status
            });
        }

        return new PackagesResponse { Packages = packages };
    }

    private async Task<Dictionary<string, ComposeProjectStatus>> GetDockerComposeStatusAsync()
    {
        try
        {
            var command = "sudo docker-compose ls --format json";
            using var client = await _sshManager.CreateSshServiceAsync();
            var ret = await client.ExecuteCommandAsync(command);
            
            if (string.IsNullOrWhiteSpace(ret.Output))
            {
                _logger.LogWarning("No output from docker-compose ls command");
                return new Dictionary<string, ComposeProjectStatus>();
            }

            var composeProjects = JsonSerializer.Deserialize<ComposeProject[]>(ret.Output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (composeProjects == null)
            {
                _logger.LogWarning("Failed to deserialize docker-compose ls output");
                return new Dictionary<string, ComposeProjectStatus>();
            }

            var statusMap = new Dictionary<string, ComposeProjectStatus>();
            foreach (var project in composeProjects)
            {
                statusMap[project.Name] = new ComposeProjectStatus
                {
                    Status = project.Status,
                    ConfigFiles = project.ConfigFiles,
                    RunningServices = project.Status.ToLowerInvariant().Contains("running") ? 1 : 0,
                    TotalServices = 1 // This is approximate, would need additional parsing for exact count
                };
            }

            _logger.LogDebug("Retrieved status for {Count} compose projects", statusMap.Count);
            return statusMap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Docker Compose status via SSH");
            return new Dictionary<string, ComposeProjectStatus>();
        }
    }

    private string GetPackageStatus(string packageName, Dictionary<string, ComposeProjectStatus> composeStatusMap)
    {
        // Try exact match first
        if (composeStatusMap.TryGetValue(packageName, out var exactMatch))
        {
            return MapComposeStatusToPackageStatus(exactMatch.Status);
        }

        // Try partial match (in case of naming differences)
        var partialMatch = composeStatusMap.FirstOrDefault(kvp => 
            kvp.Key.Contains(packageName, StringComparison.OrdinalIgnoreCase) ||
            packageName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase));

        if (!partialMatch.Equals(default(KeyValuePair<string, ComposeProjectStatus>)))
        {
            _logger.LogDebug("Found partial match for package {PackageName}: {ComposeName}", 
                packageName, partialMatch.Key);
            return MapComposeStatusToPackageStatus(partialMatch.Value.Status);
        }

        // No match found - package might not be deployed or not running
        _logger.LogDebug("No Docker Compose project found for package {PackageName}", packageName);
        return "not-deployed";
    }

    private string MapComposeStatusToPackageStatus(string composeStatus)
    {
        return composeStatus.ToLowerInvariant() switch
        {
            var status when status.Contains("running") => "running",
            var status when status.Contains("exited") => "stopped",
            var status when status.Contains("paused") => "paused",
            var status when status.Contains("restarting") => "restarting",
            var status when status.Contains("dead") => "failed",
            _ => "unknown"
        };
    }

    public async Task<UpgradeStatusResponse> GetUpgradeStatusAsync(string packageName)
    {
        var packageInfo = await _updateService.GetPackageAsync(packageName);

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

    public async Task<UpdateResponse> TriggerUpdateAsync(string packageName)
    {
        var updateId = Guid.NewGuid().ToString();
        
        // Start update in background
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Starting update for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
                await _updateService.TriggerUpdateAsync(packageName);
                _logger.LogInformation("Update completed for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update failed for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
            }
        });

        return new UpdateResponse
        {
            PackageName = packageName,
            UpdateId = updateId,
            Status = "started",
            Message = "Update process initiated"
        };
    }

    public async Task<UpdateAllResponse> TriggerUpdateAllAsync()
    {
        var updatesStarted = new List<UpdateInfo>();
        var skipped = new List<SkippedPackage>();

        var packageInfos = await _updateService.GetPackagesAsync();
        
        foreach (var packageInfo in packageInfos)
        {
            try
            {
                if (packageInfo.UpgradeAvailable)
                {
                    var updateId = Guid.NewGuid().ToString();
                    
                    // Start update in background
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _logger.LogInformation("Starting update for package {PackageName}, update ID: {UpdateId}", packageInfo.Name, updateId);
                            await _updateService.TriggerUpdateAsync(packageInfo.Name);
                            _logger.LogInformation("Update completed for package {PackageName}, update ID: {UpdateId}", packageInfo.Name, updateId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Update failed for package {PackageName}, update ID: {UpdateId}", packageInfo.Name, updateId);
                        }
                    });

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

        // If no packages to update, trigger the update process anyway
        if (updatesStarted.Count == 0)
        {
            _ = Task.Run(async () =>
            {
                await _updateService.UpdateAllAsync();
            });
        }

        return new UpdateAllResponse
        {
            UpdatesStarted = updatesStarted,
            Skipped = skipped
        };
    }
}

public class PackageNotFoundException : Exception
{
    public PackageNotFoundException(string message) : base(message) { }
}