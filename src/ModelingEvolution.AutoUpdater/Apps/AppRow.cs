using System.ComponentModel;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>The state of one app card.</summary>
public enum AppStatus
{
    Downloading,
    NotAvailableYet,
    Ready,
}

/// <summary>Where an app's install guide is.</summary>
public enum GuideState
{
    /// <summary>The app has no guide.</summary>
    None,
    Loading,
    Loaded,
    /// <summary>The guide could not be read (missing, too large, not UTF-8, the image not answering).</summary>
    Unavailable,
}

/// <summary>What the Apps page shows instead of app cards, when it has none to show.</summary>
public enum AppsPageStatus
{
    /// <summary>The read has not finished yet.</summary>
    Checking,
    /// <summary>The apps package is registered but no release of it is on this station yet.</summary>
    NotDownloaded,
    /// <summary>The apps image is not answering.</summary>
    Unavailable,
    /// <summary>The image answers with a manifest schema this AutoUpdater does not read.</summary>
    UnsupportedSchema,
    /// <summary>The image answers and lists no app.</summary>
    Empty,
    /// <summary>The image answers and lists apps: the cards show.</summary>
    Listed,
}

/// <summary>What the Apps page knows about the package itself, from the AutoUpdater's existing read model.</summary>
/// <param name="Registered">An apps package is configured (Apps:PackageName) and registered on this station.</param>
/// <param name="Deployed">A release of that package is on this station.</param>
/// <param name="UpdateInProgress">That package is being downloaded or updated now.</param>
public readonly record struct AppsPackageSnapshot(bool Registered, bool Deployed, bool UpdateInProgress)
{
    public static readonly AppsPackageSnapshot None = new(false, false, false);
}

/// <summary>One app card on the Apps page.</summary>
public sealed class AppRow : INotifyPropertyChanged
{
    public AppRow(AppEntry entry)
    {
        Entry = entry;
        Guide = entry.Guide == null ? GuideState.None : GuideState.Loading;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppEntry Entry { get; }
    public string Id => Entry.Id;
    public AppFile? File => Entry.File;
    public AppStatus Status { get; private set; } = AppStatus.NotAvailableYet;
    public GuideState Guide { get; private set; }

    /// <summary>The guide's markdown, once <see cref="Guide"/> is <see cref="GuideState.Loaded"/>.</summary>
    public string? GuideMarkdown { get; private set; }

    public bool CanDownload => Status == AppStatus.Ready;

    public void Apply(AppsPackageSnapshot package)
    {
        var status = Decide(Entry.File, package);
        if (status == Status) return;
        Status = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }

    /// <summary>The guide as read: null means it could not be read.</summary>
    public void SetGuide(string? markdown)
    {
        if (Entry.Guide == null) return;
        GuideMarkdown = markdown;
        Guide = markdown == null ? GuideState.Unavailable : GuideState.Loaded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Guide)));
    }

    public static AppStatus Decide(AppFile? file, AppsPackageSnapshot package)
    {
        if (package.UpdateInProgress) return AppStatus.Downloading;
        return file == null ? AppStatus.NotAvailableYet : AppStatus.Ready;
    }

    /// <summary>The page-level state. Cards show only for <see cref="AppsPageStatus.Listed"/>; nothing is invented otherwise.</summary>
    public static AppsPageStatus DecidePage(AppsCatalog? catalog, AppsPackageSnapshot package)
    {
        if (catalog == null) return AppsPageStatus.Checking;
        if (catalog.UnsupportedSchemaVersion != null) return AppsPageStatus.UnsupportedSchema;
        if (!catalog.Reachable) return package.Registered && !package.Deployed ? AppsPageStatus.NotDownloaded : AppsPageStatus.Unavailable;
        return catalog.Apps.IsEmpty ? AppsPageStatus.Empty : AppsPageStatus.Listed;
    }

    /// <summary>One row per app the catalog lists, in its order; none when it could not be read.</summary>
    public static IReadOnlyList<AppRow> For(AppsCatalog? catalog, AppsPackageSnapshot package)
    {
        if (catalog is not { Reachable: true }) return [];
        return catalog.Apps.Select(app =>
        {
            var row = new AppRow(app);
            row.Apply(package);
            return row;
        }).ToList();
    }
}
