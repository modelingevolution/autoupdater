using Microsoft.Extensions.Configuration;
using ModelingEvolution.AutoUpdater.Extensions;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>Where the apps package is. Read through <see cref="ConfigurationExtensions"/>; defaults match packaging/apps/compose.</summary>
public sealed record AppsOptions(string PackageName, int Port, Uri InternalBaseUrl)
{
    public static AppsOptions From(IConfiguration configuration) =>
        new(configuration.AppsPackageName(), configuration.AppsPort(), configuration.AppsInternalBaseUrl());

    /// <summary>
    /// The link a browser downloads from: the AutoUpdater's own file proxy, relative to the page, so the download comes from
    /// the same origin (scheme, host and port) the person opened the page on. The apps container is not reachable from the
    /// LAN.
    /// </summary>
    public static string DownloadPath(string fileName) => "apps/files/" + Uri.EscapeDataString(fileName);

    /// <summary>Where the AutoUpdater fetches a file from the container. Only for a name that passed <see cref="AppsManifest.IsSafeFileName"/>.</summary>
    public Uri FileUrl(string fileName) => new(InternalBaseUrl, "files/" + Uri.EscapeDataString(fileName));
}

/// <summary>Reads what the apps container offers. Never throws for a container that is down, slow or misbehaving.</summary>
public sealed class AppsCatalogClient
{
    private const int MaxDocumentBytes = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly AppsOptions _options;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Snapshot? _last;

    private sealed record Snapshot(AppsCatalog Catalog, long ReadAtMs);

    public AppsCatalogClient(IConfiguration configuration) : this(CreateHttpClient(), AppsOptions.From(configuration))
    {
    }

    /// <param name="http">
    /// Used as given, also by the file proxy. Each catalog read has its own 3 s limit; a file download has none overall
    /// (a large file takes as long as it takes), only the proxy's limit on waiting for the response headers.
    /// </param>
    public AppsCatalogClient(HttpClient http, AppsOptions options)
    {
        _http = http;
        _options = options;
    }

    public AppsOptions Options => _options;

    /// <summary>The client to the container, for the file proxy.</summary>
    public HttpClient Http => _http;

    /// <summary>
    /// No redirects and no proxy: the AutoUpdater talks to one container, and a container that answers with a redirect
    /// must not make it fetch some other address. No overall timeout, so a large download is not cut off; connecting is
    /// limited to 5 s, and every caller limits its own wait for the response.
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

    /// <summary>Reads the catalog from the container now.</summary>
    public async Task<AppsCatalog> GetAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await ReadDocumentAsync("manifest.json", cancellationToken);
        var catalog = manifest == null
            ? AppsCatalog.Unreachable
            : AppsManifest.Parse(manifest, await ReadDocumentAsync("verification.json", cancellationToken));
        // A read the caller cancelled says nothing about the container, so it is not remembered.
        if (!cancellationToken.IsCancellationRequested) Volatile.Write(ref _last, new Snapshot(catalog, Environment.TickCount64));
        return catalog;
    }

    /// <summary>
    /// The last catalog read, if it is younger than <paramref name="maxAge"/>; otherwise reads it now (one read at a time,
    /// however many callers wait). The Apps page's own refresh keeps it fresh while the page is open.
    /// </summary>
    public async Task<AppsCatalog> GetRecentAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        if (Fresh(maxAge) is { } recent) return recent;

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            return Fresh(maxAge) ?? await GetAsync(cancellationToken);
        }
        finally
        {
            _refresh.Release();
        }
    }

    private AppsCatalog? Fresh(TimeSpan maxAge)
    {
        var last = Volatile.Read(ref _last);
        return last != null && Environment.TickCount64 - last.ReadAtMs < maxAge.TotalMilliseconds ? last.Catalog : null;
    }

    private async Task<string?> ReadDocumentAsync(string name, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await _http.GetAsync(new Uri(_options.InternalBaseUrl, name), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaxDocumentBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0)
            {
                length += read;
            }

            return length > MaxDocumentBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
