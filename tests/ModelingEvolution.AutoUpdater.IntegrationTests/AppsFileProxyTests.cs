using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Apps;
using ModelingEvolution.AutoUpdater.Host.Api.Apps;
using Xunit;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

/// <summary>A Kestrel server on 127.0.0.1 and a free port, for the proxy and for a fake apps container.</summary>
public sealed class TestWebServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public IServiceProvider Services => _app.Services;

    private TestWebServer(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<TestWebServer> StartAsync(Action<IServiceCollection> services, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        services(builder.Services);
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestWebServer(app, new Uri(address.TrimEnd('/') + "/"));
    }

    /// <summary>The AutoUpdater's /apps/files/{name} endpoint, reading the catalog from and proxying to <paramref name="container"/>.</summary>
    public static Task<TestWebServer> StartProxyAsync(Uri container, AppsFileProxyLimits? limits = null) => StartAsync(
        services => services.AddApps(new AppsSettings(new AppsOptions(container), null), limits),
        app => app.MapAppsEndpoints());

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Stands in for the apps container where a test must control its timing or see every request it gets. Lists the given
/// files in its manifest.json; <see cref="Serve"/> answers for them.
/// </summary>
public sealed class FakeAppsContainer : IAsyncDisposable
{
    private TestWebServer _server = null!;

    public ConcurrentQueue<string> Requests { get; } = new();
    /// <summary>One app, "viewer", with one Windows download per file; <c>Id</c> becomes the download's label.</summary>
    public required (string Id, string Name)[] Files { get; init; }
    public required Func<HttpContext, string, Task> Serve { get; init; }
    /// <summary>The app's icon as the manifest names it, if any.</summary>
    public string? IconName { get; init; }
    /// <summary>What <c>/icons/{name}</c> answers, for any name; 404 when null.</summary>
    public byte[]? IconBytes { get; init; }
    public Uri BaseAddress => _server.BaseAddress;
    public int FileRequests => Requests.Count(p => p.StartsWith("/files/"));
    public int IconRequests => Requests.Count(p => p.StartsWith("/icons/"));

    public async Task<FakeAppsContainer> StartAsync()
    {
        var icon = IconName == null ? "" : $",\"icon\":\"{IconName}\"";
        var manifest = "{\"schemaVersion\":2,\"apps\":[{\"id\":\"viewer\",\"name\":\"Example Viewer\"" + icon + ",\"downloads\":[" +
            string.Join(",", Files.Select(f =>
                $"{{\"platform\":\"windows\",\"architecture\":\"x64\",\"label\":\"{f.Id}\",\"version\":\"1\",\"file\":\"{f.Name}\",\"size\":1}}")) + "]}]}";
        _server = await TestWebServer.StartAsync(_ => { }, app =>
        {
            app.Use(async (context, next) =>
            {
                Requests.Enqueue(context.Request.Path.Value!);
                await next();
            });
            app.MapGet("/manifest.json", () => Results.Text(manifest, "application/json"));
            app.MapMethods("/files/{name}", [HttpMethods.Get, HttpMethods.Head], (HttpContext context, string name) => Serve(context, name));
            app.MapGet("/icons/{name}", () => IconBytes == null ? Results.NotFound() : Results.Bytes(IconBytes, "image/png"));
        });
        return this;
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// A container that writes its file response as raw bytes, for answers Kestrel would refuse to send (a header value with a
/// control or non-ASCII character).
/// </summary>
public sealed class RawFakeContainer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public Uri BaseAddress => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    public static Task<RawFakeContainer> StartAsync(string fileName, string fileResponse)
    {
        var container = new RawFakeContainer();
        container._listener.Start();
        var manifest = $$"""{"schemaVersion":2,"apps":[{"id":"viewer","name":"Example Viewer","downloads":[{"platform":"windows","architecture":"x64","version":"1","file":"{{fileName}}","size":1}]}]}""";
        container._loop = container.ServeAsync(path => path switch
        {
            "/manifest.json" => Ok(manifest),
            _ => Encoding.Latin1.GetBytes(fileResponse),
        });
        return Task.FromResult(container);

        static byte[] Ok(string body) => Encoding.Latin1.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
    }

    private async Task ServeAsync(Func<string, byte[]> respond)
    {
        try
        {
            while (true)
            {
                using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = socket.GetStream();
                var reader = new StreamReader(stream, Encoding.Latin1);
                var path = (await reader.ReadLineAsync(_stop.Token) ?? "").Split(' ').ElementAtOrDefault(1) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                await stream.WriteAsync(respond(path), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
    }
}

public sealed class AppsFileProxyTests(AppsImageFixture image) : IClassFixture<AppsImageFixture>
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private Uri Healthy => new($"http://127.0.0.1:{image.HealthyPort}/");

    private static HttpClient ClientFor(TestWebServer proxy) => new() { BaseAddress = proxy.BaseAddress };

    private static async Task<int> RawStatus(Uri server, string path)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.Latin1);
        var statusLine = await reader.ReadLineAsync() ?? "";
        return int.Parse(statusLine.Split(' ')[1]);
    }

    // ---- Against the real apps image ----

    [Fact]
    public async Task Download_ThroughTheProxy_IsTheExactFile_WithTheContainersHeaders()
    {
        await using var proxy = await TestWebServer.StartProxyAsync(Healthy);
        using var client = ClientFor(proxy);
        using var direct = await image.Healthy.GetAsync($"files/{AppsImageFixture.WindowsFile}", HttpCompletionOption.ResponseHeadersRead);

        using var response = await client.GetAsync(AppsOptions.DownloadPath(AppsImageFixture.WindowsFile));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Hash(await response.Content.ReadAsByteArrayAsync()).Should().Be(Hash(image.WindowsBytes));
        response.Content.Headers.ContentLength.Should().Be(image.WindowsBytes.Length);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/x-msi");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName!.Trim('"').Should().Be(AppsImageFixture.WindowsFile);
        response.Content.Headers.LastModified.Should().Be(direct.Content.Headers.LastModified);
        response.Headers.ETag.Should().Be(direct.Headers.ETag);
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        response.Headers.AcceptRanges.Should().Contain("bytes");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
    }

    [Fact]
    public async Task Range_ThroughTheProxy_Is206_WithThoseBytes()
    {
        await using var proxy = await TestWebServer.StartProxyAsync(Healthy);
        using var client = ClientFor(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, AppsOptions.DownloadPath(AppsImageFixture.WindowsFile));
        request.Headers.Range = new RangeHeaderValue(1000, 1999);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.From.Should().Be(1000);
        response.Content.Headers.ContentRange.Length.Should().Be(image.WindowsBytes.Length);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(image.WindowsBytes.AsSpan(1000, 1000).ToArray());
    }

    [Fact]
    public async Task ResumeWithAnOldETag_ThroughTheProxy_GetsTheWholeFile()
    {
        await using var proxy = await TestWebServer.StartProxyAsync(Healthy);
        using var client = ClientFor(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, AppsOptions.DownloadPath(AppsImageFixture.WindowsFile));
        request.Headers.Range = new RangeHeaderValue(1000, null);
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"5e0be100-{image.WindowsBytes.Length:x}\""));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Hash(await response.Content.ReadAsByteArrayAsync()).Should().Be(Hash(image.WindowsBytes));
    }

    [Fact]
    public async Task Revalidation_ThroughTheProxy_Is304()
    {
        await using var proxy = await TestWebServer.StartProxyAsync(Healthy);
        using var client = ClientFor(proxy);
        using var first = await client.GetAsync(AppsOptions.DownloadPath(AppsImageFixture.WindowsFile), HttpCompletionOption.ResponseHeadersRead);
        using var request = new HttpRequestMessage(HttpMethod.Get, AppsOptions.DownloadPath(AppsImageFixture.WindowsFile));
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Head_ThroughTheProxy_HasTheLengthAndNoBody()
    {
        await using var proxy = await TestWebServer.StartProxyAsync(Healthy);
        using var client = ClientFor(proxy);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, AppsOptions.DownloadPath(AppsImageFixture.WindowsFile)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.Should().Be(image.WindowsBytes.Length);
    }

    [Fact]
    public async Task RangeBeyondTheEnd_ThroughTheProxy_Is416()
    {
        await using var proxy = await TestWebServer.StartProxyAsync(Healthy);
        using var client = ClientFor(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, AppsOptions.DownloadPath(AppsImageFixture.WindowsFile));
        request.Headers.Range = new RangeHeaderValue(image.WindowsBytes.Length + 10, null);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    // ---- Against a fake container, which shows every request it gets and controls its timing ----

    [Fact]
    public async Task ContainerAnswers503_IsPassedThrough()
    {
        // Listed in the manifest, refused by the container itself.
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "a.msi")],
            Serve = (context, _) => { context.Response.StatusCode = 503; return Task.CompletedTask; },
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress);
        using var client = ClientFor(proxy);

        using var response = await client.GetAsync(AppsOptions.DownloadPath("a.msi"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        container.FileRequests.Should().Be(1);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task ContainerAnswersSomethingElse_IsABadGateway(int status)
    {
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "a.msi")],
            Serve = (context, _) =>
            {
                context.Response.StatusCode = status;
                if (status == 302) context.Response.Headers.Location = "http://127.0.0.1:1/elsewhere";
                return Task.CompletedTask;
            },
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = proxy.BaseAddress };

        using var response = await client.GetAsync(AppsOptions.DownloadPath("a.msi"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task UnknownName_Is404_AndTheContainerIsNeverAskedForIt()
    {
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "a.msi")],
            Serve = (context, _) => context.Response.WriteAsync("x"),
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress);
        using var client = ClientFor(proxy);
        (await client.GetAsync(AppsOptions.DownloadPath("a.msi"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var before = container.Requests.Count;

        using var response = await client.GetAsync(AppsOptions.DownloadPath("other.msi"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        container.Requests.Skip(before).Should().Equal(["/manifest.json"], "the manifest is read afresh, and other.msi is not in it");
    }

    [Theory]
    [InlineData("/apps/files/..%2F..%2Fetc%2Fpasswd")]
    [InlineData("/apps/files/%2e%2e")]
    [InlineData("/apps/files/.hidden")]
    [InlineData("/apps/files/a.msi%0A")]
    [InlineData("/apps/files/a.msi%00")]
    [InlineData("/apps/files/a%20b.msi")]
    [InlineData("/apps/files/../../etc/passwd")]
    [InlineData("/apps/files/a.msi/..%2F..%2Fmanifest.json")]
    public async Task TraversalOrUnsafeName_IsRefused_WithoutAskingTheContainer(string path)
    {
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "a.msi")],
            Serve = (context, _) => context.Response.WriteAsync("x"),
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress);

        var status = await RawStatus(proxy.BaseAddress, path);

        status.Should().BeOneOf(400, 404);
        container.FileRequests.Should().Be(0);
    }

    [Fact]
    public async Task ContainerSlowToAnswer_IsAGatewayTimeout_AfterTheHeadersLimit()
    {
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "a.msi")],
            Serve = async (context, _) => await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted),
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress, AppsFileProxyLimits.Default with { HeadersTimeout = TimeSpan.FromSeconds(1) });
        using var client = ClientFor(proxy);
        var started = DateTime.UtcNow;

        using var response = await client.GetAsync(AppsOptions.DownloadPath("a.msi"));

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SlowBody_LongerThanAnyShortLimit_StillArrivesWhole()
    {
        // Headers at once, then the body over 4 s: more than the catalog's 3 s limit and the 1 s headers limit set here.
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "a.msi")],
            Serve = async (context, _) =>
            {
                context.Response.ContentLength = 4 * 1024;
                await context.Response.Body.FlushAsync();
                for (var i = 0; i < 4; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted);
                    await context.Response.Body.WriteAsync(new byte[1024], context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            },
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress, AppsFileProxyLimits.Default with { HeadersTimeout = TimeSpan.FromSeconds(1) });
        using var client = ClientFor(proxy);

        using var response = await client.GetAsync(AppsOptions.DownloadPath("a.msi"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Length.Should().Be(4 * 1024);
    }

    [Fact]
    public async Task BrowserDisconnects_MidStream_TheRequestToTheContainerEnds()
    {
        var containerSawTheEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "endless.msi")],
            Serve = async (context, _) =>
            {
                context.Response.ContentLength = 1L << 40;
                var chunk = new byte[64 * 1024];
                try
                {
                    while (true) await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                    containerSawTheEnd.TrySetResult();
                }
            },
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress);

        using (var client = ClientFor(proxy))
        using (var response = await client.GetAsync(AppsOptions.DownloadPath("endless.msi"), HttpCompletionOption.ResponseHeadersRead))
        await using (var body = await response.Content.ReadAsStreamAsync())
        {
            await body.ReadExactlyAsync(new byte[1024 * 1024]);
        }

        var ended = await Task.WhenAny(containerSawTheEnd.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        ended.Should().BeSameAs(containerSawTheEnd.Task, "the proxy must close its request to the container when the browser leaves");
    }

    [Fact]
    public async Task Body_IsPassedOnAsItArrives_NotAfterTheWholeFile()
    {
        // The container sends 1 MB, then waits. A proxy that buffered the file would deliver nothing until the gate opens.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "gate.msi")],
            Serve = async (context, _) =>
            {
                context.Response.ContentLength = 2 * 1024 * 1024;
                await context.Response.Body.WriteAsync(new byte[1024 * 1024], context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await gate.Task.WaitAsync(context.RequestAborted);
                await context.Response.Body.WriteAsync(new byte[1024 * 1024], context.RequestAborted);
            },
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress);
        using var client = ClientFor(proxy);

        using var response = await client.GetAsync(AppsOptions.DownloadPath("gate.msi"), HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await body.ReadExactlyAsync(new byte[1024 * 1024], timeout.Token);

        gate.Task.IsCompleted.Should().BeFalse("the first megabyte arrived while the container still held the rest");
        gate.SetResult();
        await body.ReadExactlyAsync(new byte[1024 * 1024], timeout.Token);
    }

    [Fact]
    public async Task DownloadsBeyondTheCap_Get503WithRetryAfter_AndTheCatalogStillReads()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "hold.msi")],
            Serve = async (context, _) =>
            {
                context.Response.ContentLength = 2048;
                await context.Response.Body.WriteAsync(new byte[1024], context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await release.Task.WaitAsync(context.RequestAborted);
                await context.Response.Body.WriteAsync(new byte[1024], context.RequestAborted);
            },
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress, AppsFileProxyLimits.Default with { MaxConcurrentDownloads = 2 });
        using var client = ClientFor(proxy);
        var path = AppsOptions.DownloadPath("hold.msi");

        // Fill the cap with downloads held open part way.
        var held = new List<HttpResponseMessage>();
        for (var i = 0; i < 2; i++)
        {
            var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            await (await response.Content.ReadAsStreamAsync()).ReadExactlyAsync(new byte[1024]);
            held.Add(response);
        }

        using var refused = await client.GetAsync(path);
        var catalog = await proxy.Services.GetRequiredService<AppsCatalogClient>().GetAsync();

        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        refused.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(AppsFileProxy.RetryAfterSeconds));
        container.FileRequests.Should().Be(2, "the refused download never reached the container");
        catalog.Reachable.Should().BeTrue();
        catalog.Files.Should().ContainSingle(f => f.FileName == "hold.msi");

        // A browser that leaves gives its slot back.
        held[0].Dispose();
        var admitted = false;
        for (var attempt = 0; attempt < 50 && !admitted; attempt++)
        {
            await Task.Delay(100);
            using var next = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            admitted = next.StatusCode == HttpStatusCode.OK;
        }
        admitted.Should().BeTrue("the slot of the download that ended is free again");

        release.SetResult();
        foreach (var response in held.Skip(1)) response.Dispose();
    }

    [Fact]
    public async Task ContainerGoesSilentMidBody_TheDownloadIsAborted_AfterTheIdleLimit()
    {
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "stall.msi")],
            Serve = async (context, _) =>
            {
                context.Response.ContentLength = 2048;
                await context.Response.Body.WriteAsync(new byte[1024], context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(TimeSpan.FromMinutes(5), context.RequestAborted);
            },
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress, AppsFileProxyLimits.Default with { IdleTimeout = TimeSpan.FromSeconds(1) });
        using var client = ClientFor(proxy);

        using var response = await client.GetAsync(AppsOptions.DownloadPath("stall.msi"), HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[1024]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var rest = async () => await body.ReadExactlyAsync(new byte[1024], timeout.Token);

        // Aborted (an I/O error the browser can resume from), not left hanging until the 15 s guard.
        (await rest.Should().ThrowAsync<Exception>()).Which.Should().NotBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task SlowBrowser_IsNotMistakenForAnIdleContainer()
    {
        // The container has everything ready; the browser stops reading for 3 s (longer than the 1 s idle limit) with far
        // more left than the socket buffers hold, so the proxy's write blocks. That is the browser's pace, not a stall.
        const long size = 64L * 1024 * 1024;
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "a.msi")],
            Serve = async (context, _) =>
            {
                context.Response.ContentLength = size;
                var chunk = new byte[64 * 1024];
                for (long sent = 0; sent < size; sent += chunk.Length)
                    await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
            },
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress, AppsFileProxyLimits.Default with { IdleTimeout = TimeSpan.FromSeconds(1) });
        using var client = ClientFor(proxy);

        using var response = await client.GetAsync(AppsOptions.DownloadPath("a.msi"), HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64 * 1024];
        long received = await body.ReadAsync(buffer);
        await Task.Delay(TimeSpan.FromSeconds(3));
        int read;
        while ((read = await body.ReadAsync(buffer)) > 0) received += read;

        received.Should().Be(size);
    }

    [Theory]
    [InlineData("ETag: \"a\u0001b\"")]
    [InlineData("Content-Disposition: attachment; filename=\"résumé.msi\"")]
    public async Task ContainerHeaderValueThatCannotBePassedOn_IsABadGateway(string badHeader)
    {
        await using var container = await RawFakeContainer.StartAsync("a.msi",
            $"HTTP/1.1 200 OK\r\nContent-Length: 1\r\n{badHeader}\r\nConnection: close\r\n\r\nx");
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress);
        using var client = ClientFor(proxy);

        using var response = await client.GetAsync(AppsOptions.DownloadPath("a.msi"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task LargeFile_256MB_StreamsWithoutHoldingItInMemory()
    {
        const long size = 256L * 1024 * 1024;
        await using var container = await new FakeAppsContainer
        {
            Files = [("windows", "big.msi")],
            Serve = async (context, _) =>
            {
                context.Response.ContentLength = size;
                var chunk = new byte[64 * 1024];
                for (long sent = 0; sent < size; sent += chunk.Length)
                    await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
            },
        }.StartAsync();
        await using var proxy = await TestWebServer.StartProxyAsync(container.BaseAddress);
        using var client = ClientFor(proxy);
        var buffer = new byte[64 * 1024];

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long received = 0, peakHeap = 0;
        using (var response = await client.GetAsync(AppsOptions.DownloadPath("big.msi"), HttpCompletionOption.ResponseHeadersRead))
        await using (var body = await response.Content.ReadAsStreamAsync())
        {
            response.Content.Headers.ContentLength.Should().Be(size);
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                received += read;
                if (received % (16 * 1024 * 1024) < read) peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
            }
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        received.Should().Be(size);
        // The whole process (client, proxy and fake container together) allocated far less than the file: nothing held it.
        allocated.Should().BeLessThan(32L * 1024 * 1024, $"{allocated / (1024 * 1024)} MB were allocated to move 256 MB");
        peakHeap.Should().BeLessThan(128L * 1024 * 1024);
    }
}

