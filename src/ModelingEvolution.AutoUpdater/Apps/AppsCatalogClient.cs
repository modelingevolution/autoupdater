using Microsoft.Extensions.Logging;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>Where the apps image is, for an enabled Apps page. See <see cref="AppsSettings"/>.</summary>
/// <param name="SourceUrl">Where the AutoUpdater reads manifest.json, guides/&lt;name&gt; and files/&lt;name&gt;.</param>
/// <param name="PackageName">The AutoUpdater package that deploys the apps image, if any: the page then shows its download state.</param>
/// <param name="Title">The page title and navigation label.</param>
public sealed record AppsOptions(Uri SourceUrl, string? PackageName = null, string Title = AppsSettings.DefaultTitle)
{
    /// <summary>
    /// The link a browser downloads from: the AutoUpdater's own file proxy, relative to the page, so the download comes from
    /// the same origin (scheme, host and port) the person opened the page on. The apps container is not reachable from the
    /// LAN.
    /// </summary>
    public static string DownloadPath(string fileName) => "apps/files/" + Uri.EscapeDataString(fileName);

    /// <summary>Where the AutoUpdater fetches a file from the container. Only for a name that passed <see cref="AppsManifest.IsSafeFileName"/>.</summary>
    public Uri FileUrl(string fileName) => new(SourceUrl, "files/" + Uri.EscapeDataString(fileName));
}

/// <summary>
/// The Apps page's settings, built once at startup from the <c>Apps:*</c> configuration values
/// (<see cref="ModelingEvolution.AutoUpdater.Extensions.ConfigurationExtensions.AppsSettings"/>). The page is off unless
/// <c>Apps:Enabled</c> is true; then <c>Apps:SourceUrl</c> (an absolute http or https URL) is required, and
/// <c>Apps:Title</c> and <c>Apps:PackageName</c> are optional.
/// </summary>
public sealed record AppsSettings(AppsOptions? Options, string? Error)
{
    public const string DefaultTitle = "Apps";
    public const int MaxTitle = 40;

    public static readonly AppsSettings Disabled = new(null, null);

    public bool Enabled => Options != null;

    /// <summary>Validates the raw <c>Apps:*</c> values. Each argument is the value as configured, or null when unset.</summary>
    public static AppsSettings Create(string? enabled, string? url, string? title, string? package)
    {
        // Deliberately not fail-fast (standards/dotnet.md): the AutoUpdater is the station's recovery path, so a bad Apps
        // value only turns the page off and is logged as an error at startup; it never stops the AutoUpdater from starting.
        if (string.IsNullOrWhiteSpace(enabled)) return Disabled;
        if (!bool.TryParse(enabled, out var on))
            return new AppsSettings(null, $"Apps:Enabled must be true or false (got '{enabled}'); the Apps page is off.");
        if (!on) return Disabled;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(source.Query) || !string.IsNullOrEmpty(source.Fragment) || !string.IsNullOrEmpty(source.UserInfo))
            return new AppsSettings(null,
                $"Apps:Enabled is true, but Apps:SourceUrl ('{url}') is not an absolute http or https URL without query or credentials; the Apps page is off.");
        // A base URL: manifest.json and files/ resolve under it, so it must end with a slash.
        if (!source.AbsolutePath.EndsWith('/')) source = new Uri(source.AbsoluteUri + "/");

        if (string.IsNullOrWhiteSpace(title)) title = DefaultTitle;
        else if (!AppsManifest.IsPlainText(title, MaxTitle))
            return new AppsSettings(null, $"Apps:Title must be plain text of at most {MaxTitle} characters; the Apps page is off.");

        return new AppsSettings(new AppsOptions(source, string.IsNullOrWhiteSpace(package) ? null : package.Trim(), title.Trim()), null);
    }

    /// <summary>Writes these settings to the startup log, and the configuration error, if any, as an error.</summary>
    public void Log(ILogger logger)
    {
        logger.LogInformation("Apps Configuration:");
        logger.LogInformation("  Apps:Enabled: {AppsEnabled}", Enabled);
        if (Options is { } options)
        {
            logger.LogInformation("  Apps:SourceUrl: {AppsSourceUrl}", options.SourceUrl);
            logger.LogInformation("  Apps:Title: {AppsTitle}", options.Title);
            logger.LogInformation("  Apps:PackageName: {AppsPackageName}", options.PackageName ?? "Not set");
        }

        if (Error != null) logger.LogError("Apps configuration error: {AppsError}", Error);
    }
}


/// <summary>
/// Reads what the apps image serves: manifest.json and the guides. Never throws for an image that is down, slow or
/// misbehaving. Nothing is cached: the Apps page reads the manifest each time it opens, and the file proxy each time it
/// is asked for a file.
/// </summary>
public sealed class AppsCatalogClient
{
    private const int MaxManifestBytes = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly AppsOptions _options;

    public AppsCatalogClient(AppsOptions options) : this(CreateHttpClient(), options)
    {
    }

    /// <param name="http">Used as given, for manifest and guide reads only; each has its own 3 s limit. The file proxy has its own client.</param>
    /// <param name="options">Where the apps image is.</param>
    public AppsCatalogClient(HttpClient http, AppsOptions options)
    {
        _http = http;
        _options = options;
    }

    public AppsOptions Options => _options;

    /// <summary>
    /// No redirects and no proxy: the AutoUpdater talks to one image, and an image that answers with a redirect must not
    /// make it fetch some other address. No overall timeout, so a large download is not cut off; connecting is limited to
    /// 5 s, and every caller limits its own wait. The catalog and the file proxy each make their own, so busy downloads
    /// never hold up a catalog read.
    /// </summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    /// <summary>Reads manifest.json now.</summary>
    public async Task<AppsCatalog> GetAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await ReadAsync("manifest.json", MaxManifestBytes, cancellationToken);
        return manifest == null ? AppsCatalog.Unreachable : AppsManifest.Parse(System.Text.Encoding.UTF8.GetString(manifest));
    }

    /// <summary>Reads one guide (<c>guides/&lt;name&gt;</c>): its markdown, or null when it cannot be read or is not acceptable.</summary>
    public async Task<string?> GetGuideAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!AppsManifest.IsGuideName(name)) return null;
        var bytes = await ReadAsync("guides/" + Uri.EscapeDataString(name), AppGuideMarkdown.MaxBytes, cancellationToken);
        // On the thread pool: the trial parse must not hold up the caller's context (a Blazor circuit).
        return bytes == null ? null : await Task.Run(() => AppGuideMarkdown.Decode(bytes), cancellationToken);
    }

    /// <summary>
    /// Reads one icon (<c>icons/&lt;name&gt;</c>): its bytes when the name is a listed-icon name and the body is a PNG of
    /// at most <see cref="AppIcon.MaxBytes"/>; otherwise null, and the page shows its neutral icon.
    /// </summary>
    public async Task<byte[]?> GetIconAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!AppsManifest.IsIconName(name)) return null;
        var bytes = await ReadAsync("icons/" + Uri.EscapeDataString(name), AppIcon.MaxBytes, cancellationToken);
        return bytes != null && AppIcon.IsPng(bytes) ? bytes : null;
    }

    /// <summary>The body, or null for any failure, a non-2xx answer, or a body over <paramref name="maxBytes"/>.</summary>
    private async Task<byte[]?> ReadAsync(string relative, int maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await _http.GetAsync(new Uri(_options.SourceUrl, relative), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[maxBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0)
            {
                length += read;
            }

            return length > maxBytes ? null : buffer[..length];
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
