using Microsoft.Extensions.Logging;

namespace ModelingEvolution.AutoUpdater;

/// <summary>
/// Last line of defence for exceptions nobody can catch (bug-019: an SSH.NET timer callback threw on a thread-pool thread and the
/// AutoUpdater stayed alive but unresponsive). An unhandled exception is logged and the process exits with code 1, so the container
/// restart policy brings it back; an unobserved task exception is logged and marked observed.
/// </summary>
/// <remarks>
/// Installed once per process: a second <see cref="Install"/> returns the instance already installed and changes nothing.
/// Use <see cref="UseLogger"/> to move the net onto the application's logger once it exists.
/// </remarks>
public sealed class ProcessSafetyNet : IDisposable
{
    /// <summary>Exit code used when an unhandled exception terminates the process.</summary>
    public const int UnhandledExceptionExitCode = 1;

    private static readonly object InstallGate = new();
    private static readonly Dictionary<object, ProcessSafetyNet> InstalledBySource = new(ReferenceEqualityComparer.Instance);

    private readonly IProcessExceptionEvents _events;
    private readonly Action<int> _exit;
    private readonly Action _blockForever;
    private readonly TimeSpan _flushTimeout;
    private volatile LogSink _sink;
    private int _exiting;
    private bool _disposed;

    private sealed record LogSink(ILogger Logger, Action? Flush);

    internal ProcessSafetyNet(ILogger logger, Action<int> exit, Action? flush, TimeSpan flushTimeout,
        IProcessExceptionEvents? events = null, Action? blockForever = null)
    {
        _sink = new LogSink(logger ?? throw new ArgumentNullException(nameof(logger)), flush);
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _flushTimeout = flushTimeout;
        _events = events ?? ProcessExceptionEvents.Instance;
        _blockForever = blockForever ?? (() => Thread.Sleep(Timeout.Infinite));
    }

    /// <summary>
    /// Subscribes to <see cref="AppDomain.UnhandledException"/> and <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// Call it first in <c>Main</c>. When a net is already installed, returns that one unchanged.
    /// </summary>
    /// <param name="logger">Receives the Critical / Error entries.</param>
    /// <param name="exit">Terminates the process; <see cref="Environment.Exit(int)"/> when not given.</param>
    /// <param name="flush">Flushes logging before exit (e.g. disposes the logger providers); bounded by <paramref name="flushTimeout"/>.</param>
    /// <param name="flushTimeout">Longest time the flush may take before the process exits anyway; 2 seconds when not given.</param>
    public static ProcessSafetyNet Install(ILogger logger, Action<int>? exit = null, Action? flush = null, TimeSpan? flushTimeout = null)
    {
        return Install(new ProcessSafetyNet(logger, exit ?? Environment.Exit, flush, flushTimeout ?? TimeSpan.FromSeconds(2)));
    }

    internal static ProcessSafetyNet Install(ProcessSafetyNet candidate)
    {
        lock (InstallGate)
        {
            if (InstalledBySource.TryGetValue(candidate._events, out var installed))
            {
                return installed;
            }

            candidate._events.Subscribe(candidate.OnUnhandledException, candidate.OnUnobservedTaskException);
            InstalledBySource[candidate._events] = candidate;
            return candidate;
        }
    }

    /// <summary>
    /// Switches the net to another logger (and its flush), e.g. from a bootstrap logger to the application's logger after the host is built.
    /// </summary>
    public void UseLogger(ILogger logger, Action? flush = null)
    {
        _sink = new LogSink(logger ?? throw new ArgumentNullException(nameof(logger)), flush);
    }

    internal void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        // Two threads can crash at once. Only the first one exits; the other must NOT return, because when an UnhandledException
        // handler returns the runtime aborts the process (SIGABRT, and as PID 1 in a container a hang or exit 139) - possibly before
        // the first thread's Environment.Exit(1) completes.
        if (Interlocked.Exchange(ref _exiting, 1) == 1)
        {
            _blockForever();
            return;
        }

        var sink = _sink;
        var exception = e.ExceptionObject as Exception;
        try
        {
            // stderr first and directly: the logging pipeline may itself be what failed, and this line must reach `docker logs`.
            Console.Error.WriteLine($"[safety-net] Unhandled exception (terminating={e.IsTerminating}); exiting with code {UnhandledExceptionExitCode}: {e.ExceptionObject}");
            if (exception is not null)
            {
                sink.Logger.LogCritical(exception, "Unhandled exception (terminating={IsTerminating}); exiting with code {ExitCode} so the restart policy recovers the process",
                    e.IsTerminating, UnhandledExceptionExitCode);
            }
            else
            {
                sink.Logger.LogCritical("Unhandled non-exception object (terminating={IsTerminating}); exiting with code {ExitCode}: {ExceptionObject}",
                    e.IsTerminating, UnhandledExceptionExitCode, e.ExceptionObject);
            }

            Flush(sink.Flush);
        }
        catch
        {
            // Logging must not stop the exit.
        }

        _exit(UnhandledExceptionExitCode);
    }

    internal void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            _sink.Logger.LogError(e.Exception, "Unobserved task exception");
        }
        catch
        {
            // Logging must not throw from the finalizer thread.
        }

        e.SetObserved();
    }

    private void Flush(Action? flush)
    {
        if (flush is null) return;
        if (!Task.Run(flush).Wait(_flushTimeout))
        {
            Console.Error.WriteLine($"[safety-net] Log flush did not finish within {_flushTimeout}; exiting anyway");
        }
    }

    /// <summary>
    /// Unsubscribes the handlers if this instance is the installed one. The net is process-wide: dispose it only at process end.
    /// </summary>
    public void Dispose()
    {
        lock (InstallGate)
        {
            if (_disposed) return;
            _disposed = true;
            if (InstalledBySource.TryGetValue(_events, out var installed) && ReferenceEquals(installed, this))
            {
                InstalledBySource.Remove(_events);
                _events.Unsubscribe(OnUnhandledException, OnUnobservedTaskException);
            }
        }
    }
}

/// <summary>
/// The process-wide exception events, behind a seam so tests can check the subscription without crashing the test host.
/// </summary>
internal interface IProcessExceptionEvents
{
    void Subscribe(UnhandledExceptionEventHandler unhandled, EventHandler<UnobservedTaskExceptionEventArgs> unobserved);

    void Unsubscribe(UnhandledExceptionEventHandler unhandled, EventHandler<UnobservedTaskExceptionEventArgs> unobserved);
}

internal sealed class ProcessExceptionEvents : IProcessExceptionEvents
{
    public static readonly ProcessExceptionEvents Instance = new();

    private ProcessExceptionEvents()
    {
    }

    public void Subscribe(UnhandledExceptionEventHandler unhandled, EventHandler<UnobservedTaskExceptionEventArgs> unobserved)
    {
        AppDomain.CurrentDomain.UnhandledException += unhandled;
        TaskScheduler.UnobservedTaskException += unobserved;
    }

    public void Unsubscribe(UnhandledExceptionEventHandler unhandled, EventHandler<UnobservedTaskExceptionEventArgs> unobserved)
    {
        AppDomain.CurrentDomain.UnhandledException -= unhandled;
        TaskScheduler.UnobservedTaskException -= unobserved;
    }
}
