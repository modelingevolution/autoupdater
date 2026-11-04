using System.ComponentModel;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>The state of one download.</summary>
public enum AppStatus
{
    Downloading,
    NotAvailableYet,
    Ready,
}

/// <summary>Where something the page reads separately (a guide, an icon) is.</summary>
public enum LoadState
{
    /// <summary>None is listed.</summary>
    None,
    Loading,
    Loaded,
    /// <summary>It could not be read or was not acceptable.</summary>
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

/// <summary>One download on an app's card, with its own status and install guide.</summary>
public sealed class DownloadRow : INotifyPropertyChanged
{
    public DownloadRow(AppDownload download)
    {
        Download = download;
        Guide = download.Guide == null ? LoadState.None : LoadState.Loading;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppDownload Download { get; }
    public AppFile? File => Download.File;
    public AppStatus Status { get; private set; } = AppStatus.NotAvailableYet;
    public LoadState Guide { get; private set; }

    /// <summary>The guide's markdown, once <see cref="Guide"/> is <see cref="LoadState.Loaded"/>.</summary>
    public string? GuideMarkdown { get; private set; }

    public bool CanDownload => Status == AppStatus.Ready;

    public void Apply(AppsPackageSnapshot package)
    {
        var status = Decide(Download.File, package);
        if (status == Status) return;
        Status = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }

    /// <summary>The guide as read: null means it could not be read.</summary>
    public void SetGuide(string? markdown)
    {
        if (Download.Guide == null) return;
        GuideMarkdown = markdown;
        Guide = markdown == null ? LoadState.Unavailable : LoadState.Loaded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Guide)));
    }

    public static AppStatus Decide(AppFile? file, AppsPackageSnapshot package)
    {
        if (package.UpdateInProgress) return AppStatus.Downloading;
        return file == null ? AppStatus.NotAvailableYet : AppStatus.Ready;
    }
}

/// <summary>One app's card on the Apps page: its icon and name, then its downloads in manifest order.</summary>
public sealed class AppCard : INotifyPropertyChanged
{
    public AppCard(AppEntry entry, AppsPackageSnapshot package)
    {
        Entry = entry;
        Icon = entry.Icon == null ? LoadState.None : LoadState.Loading;
        Downloads = entry.Downloads.Select(d => new DownloadRow(d)).ToList();
        Apply(package);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppEntry Entry { get; }
    public string Id => Entry.Id;

    /// <summary>Fixed for the card's life; each row raises its own changes.</summary>
    public IReadOnlyList<DownloadRow> Downloads { get; }

    /// <summary><see cref="LoadState.Loaded"/> once the image's icon was read and is a PNG; otherwise the page shows a neutral icon.</summary>
    public LoadState Icon { get; private set; }

    public void Apply(AppsPackageSnapshot package)
    {
        foreach (var row in Downloads) row.Apply(package);
    }

    public void SetIcon(bool available)
    {
        if (Entry.Icon == null) return;
        Icon = available ? LoadState.Loaded : LoadState.Unavailable;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    }

    /// <summary>The page-level state. Cards show only for <see cref="AppsPageStatus.Listed"/>; nothing is invented otherwise.</summary>
    public static AppsPageStatus DecidePage(AppsCatalog? catalog, AppsPackageSnapshot package)
    {
        if (catalog == null) return AppsPageStatus.Checking;
        if (catalog.UnsupportedSchemaVersion != null) return AppsPageStatus.UnsupportedSchema;
        if (!catalog.Reachable) return package.Registered && !package.Deployed ? AppsPageStatus.NotDownloaded : AppsPageStatus.Unavailable;
        return catalog.Apps.IsEmpty ? AppsPageStatus.Empty : AppsPageStatus.Listed;
    }

    /// <summary>One card per app the catalog lists, in its order; none when it could not be read.</summary>
    public static IReadOnlyList<AppCard> For(AppsCatalog? catalog, AppsPackageSnapshot package)
    {
        if (catalog is not { Reachable: true }) return [];
        return catalog.Apps.Select(app => new AppCard(app, package)).ToList();
    }
}
