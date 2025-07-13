namespace ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater.Models;

public record PackagesResponse
{
    public List<PackageStatus> Packages { get; init; } = new();
}

public record PackageStatus
{
    public string Name { get; init; } = string.Empty;
    public string RepositoryUrl { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public DateTime LastChecked { get; init; }
    public string Status { get; init; } = string.Empty;
}

public record UpgradeStatusResponse
{
    public string PackageName { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string AvailableVersion { get; init; } = string.Empty;
    public bool UpgradeAvailable { get; init; }
    public string Changelog { get; init; } = string.Empty;
}

public record UpdateResponse
{
    public string PackageName { get; init; } = string.Empty;
    public string UpdateId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public record UpdateInfo
{
    public string PackageName { get; init; } = string.Empty;
    public string UpdateId { get; init; } = string.Empty;
    public string FromVersion { get; init; } = string.Empty;
    public string ToVersion { get; init; } = string.Empty;
}

public record SkippedPackage
{
    public string PackageName { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public record UpdateAllResponse
{
    public List<UpdateInfo> UpdatesStarted { get; init; } = new();
    public List<SkippedPackage> Skipped { get; init; } = new();
}

// Models for Docker Compose status checking
public record ComposeProject
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ConfigFiles { get; init; } = string.Empty;
}

public record ComposeProjectStatus
{
    public string Status { get; init; } = string.Empty;
    public string ConfigFiles { get; init; } = string.Empty;
    public int RunningServices { get; init; }
    public int TotalServices { get; init; }
}