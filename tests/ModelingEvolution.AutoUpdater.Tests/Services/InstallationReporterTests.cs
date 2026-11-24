using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;
using ModelingEvolution.AutoUpdater.Host.Services;
using ModelingEvolution.AutoUpdater.Services;
using ModelingEvolution.AutoUpdater.Tests.Common;
using SinkEntry = ModelingEvolution.AutoUpdater.Services.LogEntry;

namespace ModelingEvolution.AutoUpdater.Tests.Services;

/// <summary>
/// The installation report (epic-092 feature-004, design.md § 6): after every installation, one POST to
/// InstallationReports:Url with package, version, succeeded, finishedAt and, on failure, reason and the log the sink
/// recorded between the start and the completion. The real EventHub and the real InMemoryLoggerSink; the receiver is a
/// recording HTTP handler. Invented values.
/// </summary>
public class InstallationReporterTests : IDisposable
{
    private const string Url = "http://10.2.0.13:5081/api/fleet/installation-reports";

    private readonly EventHub _hub = new(NullLogger<EventHub>.Instance);
    private readonly InMemoryLoggerSink _sink = new() { Enabled = false };   // as registered: off until something turns it on
    private readonly RecordingHandler _receiver = new();
    private readonly RecordingLogger<InstallationReporter> _log = new();
    private InstallationReporter? _reporter;

    private async Task<InstallationReporter> Start(string? url = Url)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(url is null ? [] : [new(InstallationReporter.UrlKey, url)])
            .Build();
        _reporter = new InstallationReporter(_hub, _sink, new Factory(_receiver), config, _log);
        await _reporter.StartAsync(default);
        return _reporter;
    }

    [Fact]
    public async Task ASuccess_PostsPackageVersionSucceededAndFinishedAt_WithNoReasonAndNoLog()
    {
        var reporter = await Start();
        await _hub.PublishAsync(new UpdateStartedEvent("rocket-welder", "v4.0.205", "v4.0.206"));
        var done = new UpdateCompletedEvent("rocket-welder", "v4.0.205", "v4.0.206", success: true);

        await _hub.PublishAsync(done);
        await reporter.LastSend;

        var request = _receiver.Requests.Should().ContainSingle().Subject;
        request.Uri.Should().Be(new Uri(Url));
        request.Method.Should().Be(HttpMethod.Post);
        var body = JsonDocument.Parse(request.Body).RootElement;
        body.GetProperty("package").GetString().Should().Be("rocket-welder");
        body.GetProperty("version").GetString().Should().Be("v4.0.206");
        body.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        body.GetProperty("finishedAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(done.CompletedAt, TimeSpan.Zero));
        body.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("log").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AFailure_PostsTheTargetVersionTheReasonAndTheLogOfThatInstallationOnly()
    {
        var reporter = await Start();
        _sink.Enabled = true;
        _sink.AddLog(LogLevel.Information, "UpdateHost", "version check before the installation");
        await Task.Delay(20);
        await _hub.PublishAsync(new UpdateStartedEvent("rocket-welder", "v4.0.206", "v4.0.207"));
        await Task.Delay(20);
        _sink.AddLog(LogLevel.Information, "UpdateHost", "Pulling rocket-welder:4.0.207");
        _sink.AddLog(LogLevel.Error, "UpdateHost", "Error response from daemon: manifest not found");
        await Task.Delay(20);

        await _hub.PublishAsync(new UpdateCompletedEvent("rocket-welder", "v4.0.206", "v4.0.207", success: false,
            errorMessage: "Docker image pull failed: manifest not found"));
        await reporter.LastSend;

        var body = JsonDocument.Parse(_receiver.Requests.Single().Body).RootElement;
        body.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        body.GetProperty("version").GetString().Should().Be("v4.0.207");
        body.GetProperty("reason").GetString().Should().Be("Docker image pull failed: manifest not found");
        var log = body.GetProperty("log").GetString()!;
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
        log.Should().Contain("Information Pulling rocket-welder:4.0.207\n")
            .And.Contain("Error Error response from daemon: manifest not found\n")
            .And.NotContain("version check before the installation", "lines before the start are not this installation's");
    }

    [Fact]
    public async Task TheStartOfAnInstallation_SwitchesTheSinkOn_SoTheLogIsRecordedWithoutTheUi()
    {
        await Start();

        await _hub.PublishAsync(new UpdateStartedEvent("rocket-welder", "v4.0.206", "v4.0.207"));

        _sink.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task NoUrl_NothingIsSent_AndTheSinkIsLeftAlone()
    {
        var reporter = await Start(url: null);

        await _hub.PublishAsync(new UpdateStartedEvent("rocket-welder", "v4.0.205", "v4.0.206"));
        await _hub.PublishAsync(new UpdateCompletedEvent("rocket-welder", "v4.0.205", "v4.0.206", success: true));
        await reporter.LastSend;

        _receiver.Requests.Should().BeEmpty();
        _sink.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task AReceiverThatRefuses_IsLoggedAndNotRetried()
    {
        _receiver.Status = HttpStatusCode.NotFound;
        _receiver.ResponseBody = """{"type":"fleet-unknown-device","address":"172.20.0.36"}""";
        var reporter = await Start();

        await _hub.PublishAsync(new UpdateCompletedEvent("rocket-welder", null, "v4.0.206", success: true));
        await reporter.LastSend;

        _receiver.Requests.Should().ContainSingle("no retry");
        _log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("404") && e.Message.Contains("172.20.0.36"));
    }

    [Fact]
    public async Task AnUnreachableReceiver_IsLogged_AndTheUpdaterIsNotFailed()
    {
        _receiver.Throw = new HttpRequestException("connection refused");
        var reporter = await Start();

        var publish = async () => await _hub.PublishAsync(new UpdateCompletedEvent("rocket-welder", null, "v4.0.206", success: true));

        await publish.Should().NotThrowAsync();
        await reporter.LastSend;
        _log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Exception is HttpRequestException);
    }

    [Fact] // the no-wait guarantee, against a receiver that blocks the calling THREAD (not an async wait)
    public async Task TheUpdaterDoesNotWaitForTheReceiver_EvenWhenTheHttpCallBlocksSynchronously()
    {
        using var gate = new ManualResetEventSlim(false);
        _receiver.BlockSynchronously = gate;
        var reporter = await Start();

        var publish = Task.Run(() => _hub.PublishAsync(new UpdateCompletedEvent("rocket-welder", null, "v4.0.206", success: true)));
        var finished = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(publish, "the completion is handled while the POST is still blocked");
        reporter.LastSend.IsCompleted.Should().BeFalse("the POST is still waiting on the receiver");
        gate.Set();
        await reporter.LastSend;
        _receiver.Requests.Should().ContainSingle();
    }

    // ── the sink drives the UI: the reporter gives back what it took, and nothing a person asked for ────────────────

    [Fact] // (1) off at start, nobody else → off again after the completion
    public async Task SinkOffAtStart_NoUi_IsOffAgainAfterTheCompletion()
    {
        var reporter = await Start();
        await _hub.PublishAsync(new UpdateStartedEvent("rocket-welder", "v4.0.206", "v4.0.207"));
        _sink.Enabled.Should().BeTrue("on during the installation, so its log is recorded");

        await _hub.PublishAsync(new UpdateCompletedEvent("rocket-welder", "v4.0.206", "v4.0.207", success: false, errorMessage: "x"));
        await reporter.LastSend;

        _sink.Enabled.Should().BeFalse();
    }

    [Fact] // (2) off at start, the UI switches it on mid-run → stays on
    public async Task SinkOffAtStart_TheUiSwitchesItOnMidRun_StaysOnAfterTheCompletion()
    {
        var reporter = await Start();
        await _hub.PublishAsync(new UpdateStartedEvent("rocket-welder", "v4.0.206", "v4.0.207"));

        _sink.Enabled = true;   // the operator opens the log / presses Update while it runs (already on: no visible change)
        await _hub.PublishAsync(new UpdateCompletedEvent("rocket-welder", "v4.0.206", "v4.0.207", success: true));
        await reporter.LastSend;

        _sink.Enabled.Should().BeTrue("the UI asked for it; the log viewer turns it off itself");
    }

    [Fact] // (3) on at start (the UI is watching) → stays on
    public async Task SinkOnAtStart_StaysOnAfterTheCompletion()
    {
        _sink.Enabled = true;
        var reporter = await Start();
        await _hub.PublishAsync(new UpdateStartedEvent("rocket-welder", "v4.0.206", "v4.0.207"));

        await _hub.PublishAsync(new UpdateCompletedEvent("rocket-welder", "v4.0.206", "v4.0.207", success: true));
        await reporter.LastSend;

        _sink.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task SinkSwitchedBackOff_StillReportsTheLogTakenBeforeIt()
    {
        var reporter = await Start();
        await _hub.PublishAsync(new UpdateStartedEvent("rocket-welder", "v4.0.206", "v4.0.207"));
        await Task.Delay(20);
        _sink.AddLog(LogLevel.Error, "UpdateHost", "Error response from daemon: manifest not found");
        await Task.Delay(20);

        await _hub.PublishAsync(new UpdateCompletedEvent("rocket-welder", "v4.0.206", "v4.0.207", success: false, errorMessage: "pull failed"));
        await reporter.LastSend;

        _sink.Enabled.Should().BeFalse();
        JsonDocument.Parse(_receiver.Requests.Single().Body).RootElement.GetProperty("log").GetString()
            .Should().Contain("manifest not found", "the log is taken before the sink goes off");
    }

    [Fact]
    public void LogText_WithoutAStart_IsEmpty()
    {
        var logs = new[] { new SinkEntry(DateTime.Now, LogLevel.Information, "c", "m") };

        InstallationReporter.LogText(logs, startedAtUtc: null, DateTime.UtcNow).Should().BeEmpty();
    }

    [Fact]
    public void LogText_ComparesTheSinksLocalStampsWithTheEventsUtcStamps()
    {
        var start = DateTime.UtcNow;
        var logs = new[]
        {
            new SinkEntry(start.AddSeconds(-1).ToLocalTime(), LogLevel.Information, "c", "before"),
            new SinkEntry(start.AddSeconds(1).ToLocalTime(), LogLevel.Information, "c", "during"),
            new SinkEntry(start.AddSeconds(3).ToLocalTime(), LogLevel.Information, "c", "after"),
        };

        InstallationReporter.LogText(logs, start, start.AddSeconds(2)).Should().EndWith(" Information during\n")
            .And.NotContain("before").And.NotContain("after");
    }

    [Fact]
    public void LogText_KeepsAnExceptionUnderItsLine()
    {
        var start = DateTime.UtcNow;
        var logs = new[] { new SinkEntry(start.AddSeconds(1).ToLocalTime(), LogLevel.Error, "c", "pull failed", new InvalidOperationException("boom")) };

        InstallationReporter.LogText(logs, start, start.AddSeconds(2)).Should().Contain("Error pull failed\n")
            .And.Contain("System.InvalidOperationException: boom");
    }

    [Fact]
    public void TheHostRegistersTheReporter_AsAHostedService_WithItsNamedClient()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        ModelingEvolution.AutoUpdater.Host.Extensions.ServiceCollectionExtensions.AddAutoUpdaterHost(services);

        services.Should().Contain(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                                       && d.ImplementationType == typeof(InstallationReporter));
    }

    public void Dispose() => _reporter?.Dispose();

    private sealed record Recorded(HttpMethod Method, Uri? Uri, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "{}";
        public Exception? Throw { get; set; }
        public Task Gate { get; set; } = Task.CompletedTask;
        public ManualResetEventSlim? BlockSynchronously { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            BlockSynchronously?.Wait(ct);   // blocks the caller's thread before any await
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add(new Recorded(request.Method, request.RequestUri, body));
            await Gate;
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(Status) { Content = new StringContent(ResponseBody) };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
