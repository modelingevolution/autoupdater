using System.Globalization;
using Microsoft.Extensions.Configuration;
using ModelingEvolution.AutoUpdater.Extensions;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>Where the apps package is. The defaults match the compose package in packaging/apps/compose.</summary>
public sealed record AppsOptions(string PackageName, int Port, Uri InternalBaseUrl)
{
    public const string DefaultPackageName = "rocketwelder-apps";
    public const int DefaultPort = 8095;
    private const string DefaultDockerHost = "172.17.0.1";

    public static AppsOptions From(IConfiguration configuration)
    {
        var name = configuration["Apps:PackageName"];
        var port = int.TryParse(configuration["Apps:Port"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536
            ? p
            : DefaultPort;

        // The AutoUpdater runs in a container: the published port is reached through the same host address as SSH.
        var host = configuration.SshHost();
        var fallback = new UriBuilder(Uri.UriSchemeHttp, string.IsNullOrWhiteSpace(host) ? DefaultDockerHost : host, port).Uri;
        var internalBaseUrl = Uri.TryCreate(configuration["Apps:InternalBaseUrl"], UriKind.Absolute, out var configured) ? configured : fallback;

        return new AppsOptions(string.IsNullOrWhiteSpace(name) ? DefaultPackageName : name, port, internalBaseUrl);
    }

    /// <summary>The address a browser downloads from: the host the person reached the page on, at the container's port.</summary>
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

    public AppsCatalogClient(IConfiguration configuration) : this(new HttpClient(), AppsOptions.From(configuration))
    {
    }

    public AppsCatalogClient(HttpClient http, AppsOptions options)
    {
        _http = http;
        _options = options;
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // Each read below has its own limit.
    }

    public AppsOptions Options => _options;

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
