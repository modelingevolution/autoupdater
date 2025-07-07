using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;

/// <summary>
/// xUnit logger provider for outputting logs to test output
/// </summary>
public class XUnitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;

    public XUnitLoggerProvider(ITestOutputHelper output)
    {
        _output = output;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new XUnitLogger(_output, categoryName);
    }

    public void Dispose() { }
}

/// <summary>
/// xUnit logger that outputs to test output
/// </summary>
public class XUnitLogger : ILogger
{
    private readonly ITestOutputHelper _output;
    private readonly string _categoryName;

    public XUnitLogger(ITestOutputHelper output, string categoryName)
    {
        _output = output;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            var message = formatter(state, exception);
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _output.WriteLine($"[{timestamp}] [{logLevel}] [{_categoryName}] {message}");
            
            if (exception != null)
            {
                _output.WriteLine($"Exception: {exception}");
            }
        }
        catch
        {
            // Ignore logging errors in tests
        }
    }

    private class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}