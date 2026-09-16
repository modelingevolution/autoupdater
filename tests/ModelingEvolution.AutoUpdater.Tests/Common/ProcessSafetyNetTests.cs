using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ModelingEvolution.AutoUpdater.Tests.Common
{
    /// <summary>
    /// bug-019: an exception nobody can catch must end the process (so the container restarts), not leave it alive and deaf.
    /// </summary>
    public class ProcessSafetyNetTests
    {
        private readonly RecordingLogger<ProcessSafetyNetTests> _logger = new();
        private readonly ConcurrentQueue<int> _exitCodes = new();

        private ProcessSafetyNet Create(Action? flush = null, TimeSpan? flushTimeout = null) =>
            new(_logger, code => _exitCodes.Enqueue(code), flush, flushTimeout ?? TimeSpan.FromSeconds(2));

        [Fact]
        public void OnUnhandledException_TimerThreadException_ExitsWithCode1AndLogsCriticalWithTheException()
        {
            var net = Create();
            var boom = new InvalidOperationException("Client not connected");

            net.OnUnhandledException(AppDomain.CurrentDomain, new UnhandledExceptionEventArgs(boom, isTerminating: true));

            _exitCodes.Should().Equal(1);
            _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical)
                .Which.Exception.Should().BeSameAs(boom);
        }

        [Fact]
        public void OnUnhandledException_FlushesLoggingBeforeExit()
        {
            var order = new ConcurrentQueue<string>();
            var net = new ProcessSafetyNet(_logger, _ => order.Enqueue("exit"), () => order.Enqueue("flush"), TimeSpan.FromSeconds(2));

            net.OnUnhandledException(this, new UnhandledExceptionEventArgs(new Exception("x"), isTerminating: true));

            order.Should().Equal("flush", "exit");
        }

        [Fact]
        public void OnUnhandledException_FlushHangs_StillExitsAfterFlushTimeout()
        {
            using var never = new ManualResetEventSlim();
            var net = Create(flush: () => never.Wait(), flushTimeout: TimeSpan.FromMilliseconds(200));
            var sw = Stopwatch.StartNew();

            net.OnUnhandledException(this, new UnhandledExceptionEventArgs(new Exception("x"), isTerminating: true));

            _exitCodes.Should().Equal(1);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
            never.Set();
        }

        [Fact]
        public void OnUnhandledException_TwoThreadsCrash_ExitsOnce()
        {
            var net = Create();

            net.OnUnhandledException(this, new UnhandledExceptionEventArgs(new Exception("a"), isTerminating: true));
            net.OnUnhandledException(this, new UnhandledExceptionEventArgs(new Exception("b"), isTerminating: true));

            _exitCodes.Should().Equal(1);
        }

        [Fact]
        public void OnUnobservedTaskException_LogsErrorMarksObserved_AndDoesNotExit()
        {
            var net = Create();
            var inner = new InvalidOperationException("fault nobody awaited");
            var args = new UnobservedTaskExceptionEventArgs(new AggregateException(inner));

            net.OnUnobservedTaskException(TaskScheduler.Default, args);

            args.Observed.Should().BeTrue();
            _exitCodes.Should().BeEmpty();
            _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
                .Which.Exception!.InnerException.Should().BeSameAs(inner);
        }

        [Fact]
        public void Install_RealUnobservedTaskException_IsDeliveredToTheLogger()
        {
            var marker = $"bug-019-{Guid.NewGuid():N}";
            using (ProcessSafetyNet.Install(_logger, code => _exitCodes.Enqueue(code)))
            {
                var deadline = Stopwatch.StartNew();
                while (!Logged(marker) && deadline.Elapsed < TimeSpan.FromSeconds(10))
                {
                    DropFaultedTask(marker);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }

            Logged(marker).Should().BeTrue("Install must subscribe the real TaskScheduler.UnobservedTaskException event");
            _exitCodes.Should().BeEmpty();
        }

        private bool Logged(string marker) =>
            _logger.Entries.Any(e => e.Exception?.InnerException?.Message == marker);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DropFaultedTask(string marker)
        {
            _ = Task.Run(() => throw new InvalidOperationException(marker));
            Thread.Sleep(50);
        }
    }
}
