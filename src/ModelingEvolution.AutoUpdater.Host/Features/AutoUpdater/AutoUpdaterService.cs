using ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater.Models;

namespace ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater;

public class AutoUpdaterService
{
    private readonly DockerComposeConfigurationRepository _repository;
    private readonly UpdateProcessManager _updateManager;
    private readonly UpdateHost _updateHost;
    private readonly ILogger<AutoUpdaterService> _logger;

    public AutoUpdaterService(
        DockerComposeConfigurationRepository repository,
        UpdateProcessManager updateManager,
        UpdateHost updateHost,
        ILogger<AutoUpdaterService> logger)
    {
        _repository = repository;
        _updateManager = updateManager;
        _updateHost = updateHost;
        _logger = logger;
    }

    public async Task<PackagesResponse> GetPackagesAsync()
    {
        var packages = new List<PackageStatus>();
        
        foreach (var config in _repository.GetPackages())
        {
            var packageName = Path.GetFileName(config.RepositoryLocation);
            var currentVersion = config.CurrentVersion;
            var lastChecked = DateTime.UtcNow; // In production, this would be tracked
            
            packages.Add(new PackageStatus
            {
                Name = packageName,
                RepositoryUrl = config.RepositoryUrl,
                CurrentVersion = currentVersion ?? "unknown",
                LastChecked = lastChecked,
                Status = "running" // In production, check actual container status
            });
        }

        return new PackagesResponse { Packages = packages };
    }

    public async Task<UpgradeStatusResponse> GetUpgradeStatusAsync(string packageName)
    {
        var config = _repository.GetPackages()
            .FirstOrDefault(c => Path.GetFileName(c.RepositoryLocation).Equals(packageName, StringComparison.OrdinalIgnoreCase));

        if (config == null)
        {
            throw new PackageNotFoundException($"Package '{packageName}' not found");
        }

        var currentVersion = config.CurrentVersion;
        var availableVersions = config.AvailableVersions(_logger);
        var latestVersion = availableVersions.OrderByDescending(v => v).FirstOrDefault();

        GitTagVersion? currentVersionParsed = null;
        if (!string.IsNullOrEmpty(currentVersion))
        {
            GitTagVersion.TryParse(currentVersion, out currentVersionParsed);
        }
        
        var upgradeAvailable = latestVersion != null && 
                             currentVersionParsed != null && 
                             latestVersion.CompareTo(currentVersionParsed) > 0;

        return new UpgradeStatusResponse
        {
            PackageName = packageName,
            CurrentVersion = currentVersion ?? "unknown",
            AvailableVersion = latestVersion?.ToString() ?? currentVersion ?? "unknown",
            UpgradeAvailable = upgradeAvailable,
            Changelog = "Bug fixes and performance improvements" // Placeholder
        };
    }

    public async Task<UpdateResponse> TriggerUpdateAsync(string packageName)
    {
        var config = _repository.GetPackages()
            .FirstOrDefault(c => Path.GetFileName(c.RepositoryLocation).Equals(packageName, StringComparison.OrdinalIgnoreCase));

        if (config == null)
        {
            throw new PackageNotFoundException($"Package '{packageName}' not found");
        }

        var updateId = Guid.NewGuid().ToString();
        
        // Start update in background
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Starting update for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
                await config.Update(_updateHost);
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

        foreach (var config in _repository.GetPackages())
        {
            var packageName = Path.GetFileName(config.RepositoryLocation);
            
            try
            {
                var currentVersion = config.CurrentVersion;
                var availableVersions = config.AvailableVersions(_logger);
                var latestVersion = availableVersions.OrderByDescending(v => v).FirstOrDefault();

                GitTagVersion? currentVersionParsed = null;
                if (!string.IsNullOrEmpty(currentVersion))
                {
                    GitTagVersion.TryParse(currentVersion, out currentVersionParsed);
                }
                
                if (latestVersion != null && 
                    currentVersionParsed != null && 
                    latestVersion.CompareTo(currentVersionParsed) > 0)
                {
                    var updateId = Guid.NewGuid().ToString();
                    
                    // Start update in background
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _logger.LogInformation("Starting update for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
                            await config.Update(_updateHost);
                            _logger.LogInformation("Update completed for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Update failed for package {PackageName}, update ID: {UpdateId}", packageName, updateId);
                        }
                    });

                    updatesStarted.Add(new UpdateInfo
                    {
                        PackageName = packageName,
                        UpdateId = updateId,
                        FromVersion = currentVersion ?? "unknown",
                        ToVersion = latestVersion.ToString()
                    });
                }
                else
                {
                    skipped.Add(new SkippedPackage
                    {
                        PackageName = packageName,
                        Reason = "No update available"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing package {PackageName}", packageName);
                skipped.Add(new SkippedPackage
                {
                    PackageName = packageName,
                    Reason = $"Error: {ex.Message}"
                });
            }
        }

        // If no packages to update, trigger the update process anyway
        if (updatesStarted.Count == 0)
        {
            _ = Task.Run(async () =>
            {
                await _updateManager.UpdateAll();
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