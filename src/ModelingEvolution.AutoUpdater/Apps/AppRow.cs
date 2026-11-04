using System.ComponentModel;

namespace ModelingEvolution.AutoUpdater.Apps;

public enum AppStatus
{
    Checking,
    NotDownloaded,
    Downloading,
    ServiceUnavailable,
    NotAvailableYet,
    ChecksumFailed,
    NotVerified,
    Ready,
}

/// <summary>What the Apps page knows about the package itself, from the AutoUpdater's existing read model.</summary>
public readonly record struct AppsPackageSnapshot(bool Deployed, bool UpdateInProgress);

/// <summary>One platform on the Apps page.</summary>
public sealed class AppRow(AppGuide guide) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public AppGuide Guide { get; } = guide;
    public AppFile? File { get; private set; }
    public AppStatus Status { get; private set; } = AppStatus.Checking;

    /// <summary>A link is only ever offered for a file the container verified and is serving.</summary>
    public bool CanDownload => Status == AppStatus.Ready;

    public void Apply(AppsCatalog? catalog, AppsPackageSnapshot package)
    {
        var file = catalog?.Find(Guide.Platform);
        var status = Decide(catalog, file, package);
        if (file == File && status == Status) return;

        File = file;
        Status = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(File)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }

    public static AppStatus Decide(AppsCatalog? catalog, AppFile? file, AppsPackageSnapshot package)
    {
        if (package.UpdateInProgress) return AppStatus.Downloading;
        if (catalog == null) return AppStatus.Checking;
        if (!catalog.Reachable) return package.Deployed ? AppStatus.ServiceUnavailable : AppStatus.NotDownloaded;
        if (file == null) return AppStatus.NotAvailableYet;
        if (file.Verified) return AppStatus.Ready;
        return catalog.VerificationKnown ? AppStatus.ChecksumFailed : AppStatus.NotVerified;
    }
}
