using Microsoft.Extensions.Logging;

namespace ModelingEvolution.AutoUpdater;

/// <summary>
/// Last line of defence for exceptions nobody can catch (bug-019: an SSH.NET timer callback threw on a thread-pool thread and the
/// AutoUpdater stayed alive but unresponsive). An unhandled exception is logged and the process exits with code 1, so the container
/// restart policy brings it back; an unobserved task exception is logged and marked observed.
/// </summary>
public sealed class ProcessSafetyNet : IDisposable
{
    /// <summary>Exit code used when an unhandled exception terminates the process.</summary>
    public const int UnhandledExceptionExitCode = 1;

    private readonly ILogger _logger;
    private readonly Action<int> _exit;
    private readonly Action? _flush;
    private readonly TimeSpan _flushTimeout;
    private int _exiting;
    private bool _installed;

    internal ProcessSafetyNet(ILogger logger, Action<int> exit, Action? flush, TimeSpan flushTimeout)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _flush = flush;
        _flushTimeout = flushTimeout;
    }

    /// <summary>
    /// Subscribes to <see cref="AppDomain.UnhandledException"/> and <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// Dispose the returned instance to unsubscribe.
    /// </summary>
    /// <param name="logger">Receives the Critical / Error entries.</param>
    /// <param name="exit">Terminates the process; <see cref="Environment.Exit(int)"/> when not given.</param>
    /// <param name="flush">Flushes logging before exit (e.g. disposes the logger factory); bounded by <paramref name="flushTimeout"/>.</param>
    /// <param name="flushTimeout">Longest time the flush may take before the process exits anyway; 2 seconds when not given.</param>
    public static ProcessSafetyNet Install(ILogger logger, Action<int>? exit = null, Action? flush = null, TimeSpan? flushTimeout = null)
    {
        var net = new ProcessSafetyNet(logger, exit ?? Environment.Exit, flush, flushTimeout ?? TimeSpan.FromSeconds(2));
        AppDomain.CurrentDomain.UnhandledException += net.OnUnhandledException;
        TaskScheduler.UnobservedTaskException += net.OnUnobservedTaskException;
        net._installed = true;
        return net;
    }

    internal void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        // Two threads can crash at once; exit only once.
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;

        var exception = e.ExceptionObject as Exception;
        try
        {
            Console.Error.WriteLine($"[safety-net] Unhandled exception (terminating={e.IsTerminating}); exiting with code {UnhandledExceptionExitCode}: {e.ExceptionObject}");
            _logger.LogCritical(exception, "Unhandled exception (terminating={IsTerminating}); exiting with code {ExitCode} so the restart policy recovers the process: {Exception}",
                e.IsTerminating, UnhandledExceptionExitCode, exception is null ? e.ExceptionObject : exception.Message);
            Flush();
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
            _logger.LogError(e.Exception, "Unobserved task exception");
        }
        catch
        {
            // Logging must not throw from the finalizer thread.
        }

        e.SetObserved();
    }

    private void Flush()
    {
        if (_flush is null) return;
        if (!Task.Run(_flush).Wait(_flushTimeout))
        {
            Console.Error.WriteLine($"[safety-net] Log flush did not finish within {_flushTimeout}; exiting anyway");
        }
    }

    public void Dispose()
    {
        if (!_installed) return;
        _installed = false;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }
}
