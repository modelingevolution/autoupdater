using ModelingEvolution.AutoUpdater.Common;

namespace ModelingEvolution.AutoUpdater.Host.Api.AutoUpdater.Models;

public record PackagesResponse
{
    public List<PackageStatus> Packages { get; init; } = new();
}

public record PackageStatus
{
    public PackageName Name { get; init; } = PackageName.Empty;
    public string RepositoryUrl { get; init; } = string.Empty;
    public PackageVersion? CurrentVersion { get; init; }
    public DateTime LastChecked { get; init; }
    public string Status { get; init; } = string.Empty;
}

public record UpgradeStatusResponse
{
    public PackageName PackageName { get; init; } = PackageName.Empty;
    public PackageVersion? CurrentVersion { get; init; }
    public PackageVersion AvailableVersion { get; init; } = PackageVersion.Empty;
    public bool UpgradeAvailable { get; init; }
    public string Changelog { get; init; } = string.Empty;
}

public record UpdateResponse
{
    public PackageName PackageName { get; init; } = PackageName.Empty;
    public string UpdateId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public record UpdateInfo
{
    public PackageName PackageName { get; init; } = PackageName.Empty;
    public string UpdateId { get; init; } = string.Empty;
    public PackageVersion? FromVersion { get; init; }
    public PackageVersion ToVersion { get; init; } = PackageVersion.Empty;
}

public record SkippedPackage
{
    public PackageName PackageName { get; init; } = PackageName.Empty;
    public string Reason { get; init; } = string.Empty;
}

public record UpdateAllResponse
{
    public List<UpdateInfo> UpdatesStarted { get; init; } = new();
    public List<SkippedPackage> Skipped { get; init; } = new();
}

public record UpdateProcessResult
{
    public List<UpdateInfo> UpdatesStarted { get; init; } = new();
    public List<SkippedPackage> Skipped { get; init; } = new();
}

// Models for Docker Compose status checking
public record ComposeProject
{
    public PackageName Name { get; init; } = PackageName.Empty;
    public string Status { get; init; } = string.Empty;
    public string ConfigFiles { get; init; } = string.Empty;
}

//public record ComposeProjectStatus
//{
//    public string Status { get; init; } = string.Empty;
//    public string ConfigFiles { get; init; } = string.Empty;
//    public int RunningServices { get; init; }
//    public int TotalServices { get; init; }
//}