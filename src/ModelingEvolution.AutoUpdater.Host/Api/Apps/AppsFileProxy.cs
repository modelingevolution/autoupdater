using System.Buffers;
using Microsoft.AspNetCore.Http.Features;
using ModelingEvolution.AutoUpdater.Apps;

namespace ModelingEvolution.AutoUpdater.Host.Api.Apps;

/// <summary>The proxy's limits. Defaults suit a station; tests pass smaller ones.</summary>
/// <param name="MaxConcurrentDownloads">Downloads in flight at once; one more gets 503 with Retry-After at once.</param>
/// <param name="HeadersTimeout">How long to wait for the container's response headers.</param>
/// <param name="IdleTimeout">How long the container may send nothing in the middle of a body before the download is aborted.</param>
public sealed record AppsFileProxyLimits(int MaxConcurrentDownloads, TimeSpan HeadersTimeout, TimeSpan IdleTimeout)
{
    public static readonly AppsFileProxyLimits Default = new(16, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
}

/// <summary>
/// Serves the apps container's files on the AutoUpdater's own origin, so a browser downloads from the page it opened and the
/// container need not be reachable from the LAN. It streams: the body is copied from the container to the browser as it
/// arrives, never held in memory, and the request to the container ends when the browser goes away.
/// </summary>
/// <remarks>
/// Only a name listed in the image's manifest.json, read afresh for each request, is fetched; anything else is a 404 and
/// the container is never asked for it. The container's answer is passed through as it is.
/// Downloads use their own HTTP client and are capped (<see cref="AppsFileProxyLimits.MaxConcurrentDownloads"/>, far below
/// nginx's 256 connections), so however many slow downloads are open, the Apps page's catalog reads still get through.
/// </remarks>
public sealed class AppsFileProxy
{
    public const int RetryAfterSeconds = 30;

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
    private readonly AppsFileProxyLimits _limits;
    private readonly HttpClient _files = AppsCatalogClient.CreateHttpClient();
    private readonly SemaphoreSlim _slots;

    public AppsFileProxy(AppsCatalogClient catalog, ILogger<AppsFileProxy> log, AppsFileProxyLimits? limits = null)
    {
        _catalog = catalog;
        _log = log;
        _limits = limits ?? AppsFileProxyLimits.Default;
        _slots = new SemaphoreSlim(_limits.MaxConcurrentDownloads, _limits.MaxConcurrentDownloads);
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
            catalog = await _catalog.GetAsync(aborted);
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

        if (!catalog.Files.Any(f => f.FileName == name))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!_slots.Wait(0))
        {
            _log.LogWarning("Refused a download of {File}: {Max} downloads are already running", name, _limits.MaxConcurrentDownloads);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
            return;
        }

        try
        {
            await ProxyAsync(context, name, aborted);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task ProxyAsync(HttpContext context, string name, CancellationToken aborted)
    {
        var head = HttpMethods.IsHead(context.Request.Method);
        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, _catalog.Options.FileUrl(name));
        foreach (var header in ForwardedRequestHeaders)
        {
            if (context.Request.Headers.TryGetValue(header, out var values))
                request.Headers.TryAddWithoutValidation(header, (IEnumerable<string?>)values);
        }

        // One timer, linked to the browser leaving: first the limit on waiting for headers, then, re-armed before each read,
        // the limit on the container sending nothing.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        limit.CancelAfter(_limits.HeadersTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _files.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("The apps container did not answer for {File} within {Timeout}", name, _limits.HeadersTimeout);
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

            if (!TryCopyHeaders(response, context.Response))
            {
                _log.LogWarning("The apps container sent a header value that cannot be passed on, for {File}", name);
                context.Response.Headers.Clear();
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            context.Response.StatusCode = status;
            if (head || status == StatusCodes.Status304NotModified) return;

            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await CopyBodyAsync(context, response, name, limit, aborted);
        }
    }

    /// <summary>The allow-listed headers. False if one has a value Kestrel refuses (a control or non-ASCII character).</summary>
    private static bool TryCopyHeaders(HttpResponseMessage from, HttpResponse to)
    {
        try
        {
            foreach (var header in ReturnedResponseHeaders)
            {
                if (from.Headers.TryGetValues(header, out var values) || from.Content.Headers.TryGetValues(header, out values))
                    to.Headers[header] = values.ToArray();
            }

            if (from.Content.Headers.ContentLength is { } length) to.ContentLength = length;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task CopyBodyAsync(HttpContext context, HttpResponseMessage response, string name, CancellationTokenSource idle,
        CancellationToken aborted)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            idle.CancelAfter(_limits.IdleTimeout);
            await using var body = await response.Content.ReadAsStreamAsync(idle.Token);
            while (true)
            {
                idle.CancelAfter(_limits.IdleTimeout);
                var read = await body.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), idle.Token);
                // The idle limit is the container's; a browser slow to take the bytes is Kestrel's business, not a stall.
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0) return;
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), aborted);
            }
        }
        catch (Exception ex) when (aborted.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            // The browser left. Disposing the response ends the request to the container.
        }
        catch (OperationCanceledException)
        {
            // The container stopped sending without closing. Abort, so the browser sees a broken download it can resume.
            _log.LogWarning("The apps container sent nothing for {Timeout} in the middle of {File}", _limits.IdleTimeout, name);
            context.Abort();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // The container stopped part way. Abort, so the browser sees a broken download, not a short complete one.
            _log.LogWarning(ex, "The apps container stopped sending {File} part way", name);
            context.Abort();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
