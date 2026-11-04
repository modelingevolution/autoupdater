using Microsoft.AspNetCore.Http.Features;
using ModelingEvolution.AutoUpdater.Apps;

namespace ModelingEvolution.AutoUpdater.Host.Api.Apps;

/// <summary>
/// Serves the apps container's files on the AutoUpdater's own origin, so a browser downloads from the page it opened and the
/// container need not be reachable from the LAN. It streams: the body is copied from the container to the browser as it
/// arrives, never held in memory, and the request to the container ends when the browser goes away.
/// </summary>
/// <remarks>
/// Only a name in the current verified catalog is fetched; anything else is a 404 and the container is never asked for it.
/// The container still applies its own rules (fail closed, 503 for a file that failed its check), and its answer is passed
/// through as it is.
/// </remarks>
public sealed class AppsFileProxy
{
    public static readonly TimeSpan DefaultHeadersTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How old the catalog used to check a name may be. The Apps page refreshes it every 10 s while it is open.</summary>
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromSeconds(10);

    private const int CopyBufferBytes = 64 * 1024;

    // Conditional and range headers, so a resume or a revalidation reaches the container unchanged.
    private static readonly string[] ForwardedRequestHeaders = ["Range", "If-Range", "If-None-Match", "If-Modified-Since"];

    private static readonly string[] ReturnedResponseHeaders =
    [
        "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Cache-Control", "Content-Disposition", "Content-Type",
        "X-Content-Type-Options",
    ];

    private static readonly HashSet<int> PassedThroughStatuses = [200, 206, 304, 416, 503];

    private readonly AppsCatalogClient _catalog;
    private readonly ILogger<AppsFileProxy> _log;
    private readonly TimeSpan _headersTimeout;

    /// <param name="headersTimeout">How long to wait for the container's response headers. The body has no limit.</param>
    public AppsFileProxy(AppsCatalogClient catalog, ILogger<AppsFileProxy> log, TimeSpan? headersTimeout = null)
    {
        _catalog = catalog;
        _log = log;
        _headersTimeout = headersTimeout ?? DefaultHeadersTimeout;
    }

    public async Task HandleAsync(HttpContext context, string name)
    {
        var aborted = context.RequestAborted;
        if (!AppsManifest.IsSafeFileName(name))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        AppsCatalog catalog;
        try
        {
            catalog = await _catalog.GetRecentAsync(CatalogMaxAge, aborted);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            return;
        }

        if (!catalog.Reachable)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (!catalog.Files.Any(f => f.Verified && f.FileName == name))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var head = HttpMethods.IsHead(context.Request.Method);
        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, _catalog.Options.FileUrl(name));
        foreach (var header in ForwardedRequestHeaders)
        {
            if (context.Request.Headers.TryGetValue(header, out var values))
                request.Headers.TryAddWithoutValidation(header, (IEnumerable<string?>)values);
        }

        // Waiting for the headers is limited; once they are here, only the browser leaving ends the transfer.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        limit.CancelAfter(_headersTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _catalog.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("The apps container did not answer for {File} within {Timeout}", name, _headersTimeout);
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            return;
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Could not reach the apps container for {File}", name);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (response)
        {
            limit.CancelAfter(Timeout.InfiniteTimeSpan);

            var status = (int)response.StatusCode;
            if (!PassedThroughStatuses.Contains(status))
            {
                _log.LogWarning("The apps container answered {Status} for {File}", status, name);
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            context.Response.StatusCode = status;
            foreach (var header in ReturnedResponseHeaders)
            {
                if (response.Headers.TryGetValues(header, out var values) || response.Content.Headers.TryGetValues(header, out values))
                    context.Response.Headers[header] = values.ToArray();
            }

            if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
            if (head || status == StatusCodes.Status304NotModified) return;

            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(aborted);
                await body.CopyToAsync(context.Response.Body, CopyBufferBytes, aborted);
            }
            catch (Exception ex) when (aborted.IsCancellationRequested && ex is OperationCanceledException or IOException)
            {
                // The browser left. Disposing the response ends the request to the container.
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                // The container stopped part way. Abort, so the browser sees a broken download, not a short complete one.
                _log.LogWarning(ex, "The apps container stopped sending {File} part way", name);
                context.Abort();
            }
        }
    }
}

public static class AppsEndpoints
{
    /// <summary>GET and HEAD <c>/apps/files/{name}</c>: the apps container's file, through <see cref="AppsFileProxy"/>.</summary>
    public static IEndpointRouteBuilder MapAppsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/apps/files/{name}", [HttpMethods.Get, HttpMethods.Head],
                (HttpContext context, string name, AppsFileProxy proxy) => proxy.HandleAsync(context, name))
            .ExcludeFromDescription();
        return app;
    }
}
