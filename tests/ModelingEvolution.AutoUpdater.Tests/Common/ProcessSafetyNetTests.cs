using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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
            var net = new ProcessSafetyNet(_logger, code => _exitCodes.Enqueue(code), null, TimeSpan.FromSeconds(2), blockForever: () => { });

            net.OnUnhandledException(this, new UnhandledExceptionEventArgs(new Exception("a"), isTerminating: true));
            net.OnUnhandledException(this, new UnhandledExceptionEventArgs(new Exception("b"), isTerminating: true));

            _exitCodes.Should().Equal(1);
        }

        [Fact]
        public async Task OnUnhandledException_SecondThreadCrashesWhileFirstIsExiting_SecondNeverReturns()
        {
            using var flushGate = new ManualResetEventSlim();
            using var loserBlocked = new ManualResetEventSlim();
            using var releaseLoser = new ManualResetEventSlim();
            Task? loser = null;
            bool? loserReturnedBeforeExit = null;
            var net = new ProcessSafetyNet(_logger,
                exit: code =>
                {
                    loserReturnedBeforeExit = loser!.IsCompleted;
                    _exitCodes.Enqueue(code);
                },
                flush: () => flushGate.Wait(TimeSpan.FromSeconds(10)),
                flushTimeout: TimeSpan.FromSeconds(10),
                events: new FakeExceptionEvents(),
                blockForever: () =>
                {
                    loserBlocked.Set();
                    releaseLoser.Wait(TimeSpan.FromSeconds(30));
                });

            var winner = Task.Run(() => net.OnUnhandledException(this, new UnhandledExceptionEventArgs(new Exception("first"), true)));
            SpinWait.SpinUntil(() => _logger.Entries.Any(e => e.Level == LogLevel.Critical), TimeSpan.FromSeconds(5)).Should().BeTrue();
            loser = Task.Run(() => net.OnUnhandledException(this, new UnhandledExceptionEventArgs(new Exception("second"), true)));

            // The loser must be parked, not returned: a returning UnhandledException handler lets the runtime abort the process.
            WaitHandle.WaitAny(new[] { loserBlocked.WaitHandle, ((IAsyncResult)loser).AsyncWaitHandle }, TimeSpan.FromSeconds(5));
            flushGate.Set();
            await winner.WaitAsync(TimeSpan.FromSeconds(10));

            loserReturnedBeforeExit.Should().BeFalse();
            loserBlocked.IsSet.Should().BeTrue();
            _exitCodes.Should().Equal(1);
            releaseLoser.Set();
            await loser.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void Install_SubscribesBothEvents_AndARaisedUnhandledExceptionExits()
        {
            var events = new FakeExceptionEvents();

            using var net = ProcessSafetyNet.Install(Candidate(events));
            events.RaiseUnhandled(new InvalidOperationException("timer thread"));

            events.Subscriptions.Should().Be(1);
            _exitCodes.Should().Equal(1);
        }

        [Fact]
        public void Install_Twice_IsANoOpReturningTheInstalledNet()
        {
            var events = new FakeExceptionEvents();

            using var first = ProcessSafetyNet.Install(Candidate(events));
            var second = ProcessSafetyNet.Install(Candidate(events));
            events.RaiseUnhandled(new Exception("x"));

            second.Should().BeSameAs(first);
            events.Subscriptions.Should().Be(1);
            _exitCodes.Should().Equal(1);
        }

        [Fact]
        public void Install_ConcurrentCalls_SubscribeOnce()
        {
            var events = new FakeExceptionEvents();

            var nets = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(16)
                .Select(_ => ProcessSafetyNet.Install(Candidate(events))).ToArray();

            nets.Distinct().Should().ContainSingle();
            events.Subscriptions.Should().Be(1);
            nets[0].Dispose();
            events.Subscriptions.Should().Be(0);
        }

        [Fact]
        public void UseLogger_SwitchesWhereTheCriticalEntryGoes()
        {
            var appLogger = new RecordingLogger<ProcessSafetyNetTests>();
            var flushed = false;
            var net = Create();

            net.UseLogger(appLogger, () => flushed = true);
            net.OnUnhandledException(this, new UnhandledExceptionEventArgs(new Exception("x"), true));

            appLogger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical);
            _logger.Entries.Should().BeEmpty();
            flushed.Should().BeTrue();
        }

        [Fact]
        public void ProcessExceptionEvents_Subscribe_AddsTheHandlerToTheRealAppDomainEvent()
        {
            UnhandledExceptionEventHandler handler = (_, _) => { };
            EventHandler<UnobservedTaskExceptionEventArgs> unobserved = (_, _) => { };

            ProcessExceptionEvents.Instance.Subscribe(handler, unobserved);
            try
            {
                UnhandledExceptionHandlers().Should().Contain(handler);
            }
            finally
            {
                ProcessExceptionEvents.Instance.Unsubscribe(handler, unobserved);
            }

            UnhandledExceptionHandlers().Should().NotContain(handler);
        }

        /// <summary>
        /// The runtime keeps AppDomain.UnhandledException subscribers in a static delegate field on AppContext. Raising the event
        /// in-process would kill the test host, so the subscription is read back instead. Fails loudly if the field moves.
        /// </summary>
        private static Delegate[] UnhandledExceptionHandlers()
        {
            var fields = typeof(AppContext).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(UnhandledExceptionEventHandler))
                .ToArray();
            fields.Should().ContainSingle("the runtime stores UnhandledException subscribers in one AppContext field");
            return (fields[0].GetValue(null) as Delegate)?.GetInvocationList() ?? Array.Empty<Delegate>();
        }

        private ProcessSafetyNet Candidate(FakeExceptionEvents events) =>
            new(_logger, code => _exitCodes.Enqueue(code), null, TimeSpan.FromSeconds(2), events);

        private sealed class FakeExceptionEvents : IProcessExceptionEvents
        {
            private UnhandledExceptionEventHandler? _unhandled;
            private int _subscriptions;

            public int Subscriptions => _subscriptions;

            public void Subscribe(UnhandledExceptionEventHandler unhandled, EventHandler<UnobservedTaskExceptionEventArgs> unobserved)
            {
                Interlocked.Increment(ref _subscriptions);
                _unhandled += unhandled;
            }

            public void Unsubscribe(UnhandledExceptionEventHandler unhandled, EventHandler<UnobservedTaskExceptionEventArgs> unobserved)
            {
                Interlocked.Decrement(ref _subscriptions);
                _unhandled -= unhandled;
            }

            public void RaiseUnhandled(Exception ex) => _unhandled?.Invoke(this, new UnhandledExceptionEventArgs(ex, true));
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
