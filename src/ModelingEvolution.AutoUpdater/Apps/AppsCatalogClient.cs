using Microsoft.Extensions.Configuration;
using ModelingEvolution.AutoUpdater.Extensions;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>Where the apps package is. Read through <see cref="ConfigurationExtensions"/>; defaults match packaging/apps/compose.</summary>
public sealed record AppsOptions(string PackageName, int Port, Uri InternalBaseUrl)
{
    public static AppsOptions From(IConfiguration configuration) =>
        new(configuration.AppsPackageName(), configuration.AppsPort(), configuration.AppsInternalBaseUrl());

    /// <summary>
    /// The address a browser downloads from: the host the person reached the page on, at the container's port.
    /// Always http: the container serves plain http on the LAN, so a page served over https would be blocked as mixed content.
    /// </summary>
    public string DownloadUrl(Uri pageUri, string fileName) =>
        new UriBuilder(Uri.UriSchemeHttp, pageUri.Host, Port, "/files/" + Uri.EscapeDataString(fileName)).Uri.AbsoluteUri;
}

/// <summary>Reads what the apps container offers. Never throws for a container that is down, slow or misbehaving.</summary>
public sealed class AppsCatalogClient
{
    private const int MaxDocumentBytes = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly AppsOptions _options;

    public AppsCatalogClient(IConfiguration configuration) : this(CreateHttpClient(), AppsOptions.From(configuration))
    {
    }

    /// <param name="http">Used as given: each read has its own 3 s limit, whatever the client's Timeout.</param>
    public AppsCatalogClient(HttpClient http, AppsOptions options)
    {
        _http = http;
        _options = options;
    }

    public AppsOptions Options => _options;

    /// <summary>
    /// No redirects and no proxy: the AutoUpdater reads two documents from one container, and a container that answers with a
    /// redirect must not make it fetch some other address.
    /// </summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    public async Task<AppsCatalog> GetAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await ReadDocumentAsync("manifest.json", cancellationToken);
        if (manifest == null) return AppsCatalog.Unreachable;

        return AppsManifest.Parse(manifest, await ReadDocumentAsync("verification.json", cancellationToken));
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
