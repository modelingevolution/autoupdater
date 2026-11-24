using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;
using ModelingEvolution.AutoUpdater.Services;

namespace ModelingEvolution.AutoUpdater.Host.Services;

/// <summary>
/// The installation report: after every installation this updater ran, success or failure, POST what happened to the
/// URL configured at <c>InstallationReports:Url</c> (absent = off, nothing is sent). A webhook in the autoupdater's own
/// words — package, version, succeeded, finishedAt, reason, log — so it learns nothing about whoever receives it (the
/// ERP maps it on its side). epic-092 feature-004, design.md § 6.
///
/// <para>Hook: <see cref="UpdateCompletedEvent"/>, which <c>UpdateHost</c> publishes for every installation that started
/// (success, partial success, every early failure, an unexpected error). "Already at latest" and "already in progress"
/// publish nothing, so nothing is reported. A self-update ends in a restart before any completion event, so it sends no
/// report (owner Q5); the new version reaches the receiver through its own poll.</para>
///
/// <para>Log (failures only): the in-memory log sink's entries from the matching <see cref="UpdateStartedEvent"/> to
/// <see cref="UpdateCompletedEvent.CompletedAt"/>. The sink is registered DISABLED and only the UI's buttons switch it on,
/// so the start of an installation switches it on, as the Update button already does.</para>
///
/// <para>One POST per installation, NOT awaited by the event handler (EventHub awaits its handlers, and the updater must
/// not wait on the receiver), no retry: a failed POST is logged and dropped (retries are Deferred).</para>
/// </summary>
public sealed class InstallationReporter : IHostedService, IDisposable
{
    public const string HttpClientName = "InstallationReports";
    public const string UrlKey = "InstallationReports:Url";

    private readonly IEventHub _hub;
    private readonly IInMemoryLoggerSink _sink;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<InstallationReporter> _log;
    private readonly Uri? _url;
    private readonly ConcurrentDictionary<string, DateTime> _startedAtUtc = new();
    private SubscriptionSet? _subscriptions;
    private Task _lastSend = Task.CompletedTask;

    public InstallationReporter(IEventHub hub, IInMemoryLoggerSink sink, IHttpClientFactory http, IConfiguration config,
        ILogger<InstallationReporter> log)
    {
        _hub = hub;
        _sink = sink;
        _http = http;
        _log = log;
        var url = config[UrlKey];
        _url = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;
        if (!string.IsNullOrWhiteSpace(url) && _url is null)
            _log.LogWarning("{Key} is not an absolute URL ({Url}); installation reports are off.", UrlKey, url);
    }

    /// <summary>The last POST started, for tests to await.</summary>
    internal Task LastSend => _lastSend;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_url is null)
        {
            _log.LogInformation("Installation reports are off ({Key} not set).", UrlKey);
            return Task.CompletedTask;
        }

        _subscriptions = _hub.Subscribe<UpdateStartedEvent>(OnStarted) + _hub.Subscribe<UpdateCompletedEvent>(OnCompleted);
        _log.LogInformation("Installation reports go to {Url}.", _url);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnStarted(UpdateStartedEvent e)
    {
        _startedAtUtc[e.ApplicationName] = e.StartedAt;
        _sink.Enabled = true;
    }

    private void OnCompleted(UpdateCompletedEvent e)
    {
        _startedAtUtc.TryRemove(e.ApplicationName, out var started);
        var report = Build(e, started == default ? null : started, e.Success ? [] : Snapshot());
        _lastSend = SendAsync(report);
    }

    /// <summary>The body: <see cref="UpdateCompletedEvent"/> plus, on failure, the installation's log.</summary>
    internal static InstallationReport Build(UpdateCompletedEvent e, DateTime? startedAtUtc, IReadOnlyList<LogEntry> logs) =>
        new(e.ApplicationName,
            e.NewVersion,
            e.Success,
            new DateTimeOffset(DateTime.SpecifyKind(e.CompletedAt, DateTimeKind.Utc)),
            e.Success ? null : e.ErrorMessage ?? "",
            e.Success ? null : LogText(logs, startedAtUtc, e.CompletedAt));

    /// <summary>The lines logged between the start and the completion, newline-joined; "" when the start was not seen.</summary>
    internal static string LogText(IReadOnlyList<LogEntry> logs, DateTime? startedAtUtc, DateTime completedAtUtc)
    {
        if (startedAtUtc is not { } from) return "";
        var sb = new StringBuilder();
        foreach (var entry in logs)
        {
            var at = entry.Timestamp.ToUniversalTime();   // the sink stamps local time; the events stamp UTC
            if (at < from || at > completedAtUtc) continue;
            sb.Append(entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(' ')
              .Append(entry.Level).Append(' ').Append(entry.Message).Append('\n');
            if (entry.Exception is not null) sb.Append(entry.Exception).Append('\n');
        }
        return sb.ToString();
    }

    private IReadOnlyList<LogEntry> Snapshot()
    {
        try { return _sink.LogEntries.ToArray(); }
        catch (Exception ex)
        {
            // The sink is an ObservableCollection written by every logger; a copy can race a write. The report still goes.
            _log.LogWarning(ex, "Could not copy the log for the installation report; sending it without the log.");
            return [];
        }
    }

    private async Task SendAsync(InstallationReport report)
    {
        await Task.Yield();   // off the event handler: the updater does not wait for the receiver
        try
        {
            using var response = await _http.CreateClient(HttpClientName).PostAsJsonAsync(_url, report);
            if (response.IsSuccessStatusCode)
                _log.LogInformation("Installation report sent: {Package} {Version} succeeded={Succeeded}.",
                    report.Package, report.Version, report.Succeeded);
            else
                _log.LogWarning("Installation report for {Package} {Version} refused: {Status} {Body}", report.Package,
                    report.Version, (int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Installation report for {Package} {Version} not sent (no retry).", report.Package, report.Version);
        }
    }

    public void Dispose() => _subscriptions?.Dispose();
}

/// <summary>The report's body on the wire (camelCase JSON): package, version (the target version on failure),
/// succeeded, finishedAt, reason and log (null on success).</summary>
public sealed record InstallationReport(string Package, string Version, bool Succeeded, DateTimeOffset FinishedAt,
    string? Reason, string? Log);
