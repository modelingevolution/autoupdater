using FluentAssertions;
using ModelingEvolution.AutoUpdater.Services;
using ModelingEvolution.AutoUpdater.Tests.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    /// <summary>
    /// bug-019: the command timeout is a token owned by <see cref="SshService"/>, and the connections are never released
    /// under a running command.
    /// </summary>
    public class SshServiceTimeoutTests
    {
        private readonly ConcurrentQueue<string> _journal = new();
        private readonly FakeCommandFactory _factory;
        private readonly SshService _sut;

        public SshServiceTimeoutTests()
        {
            _factory = new FakeCommandFactory(_journal);
            _sut = new SshService(_factory, null, null, () => _journal.Enqueue("clients disposed"), new RecordingLogger<SshService>())
            {
                CancelGrace = TimeSpan.FromMilliseconds(200),
                OutputDrainTimeout = TimeSpan.FromMilliseconds(200),
                DisposeDrainTimeout = TimeSpan.FromSeconds(5)
            };
        }

        [Fact]
        public async Task ExecuteCommandAsync_CommandNeverCompletes_ReturnsTimedOutFailureAfterTimeout()
        {
            _factory.Behaviour = FakeBehaviour.RunsUntilCancelled;
            var sw = Stopwatch.StartNew();

            var result = await _sut.ExecuteCommandAsync("sleep 30", TimeSpan.FromMilliseconds(300)).WaitAsync(TimeSpan.FromSeconds(10));

            sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
            result.IsSuccess.Should().BeFalse();
            result.ExitCode.Should().Be(-1);
            result.Error.Should().Be("Command timed out after 0.3 seconds");
            _journal.Should().Equal("cancel: sleep 30", "handle disposed: sleep 30");
        }

        [Fact]
        public async Task ExecuteCommandAsync_CommandIgnoresCancellation_StillReturnsTimedOutFailure()
        {
            _factory.Behaviour = FakeBehaviour.IgnoresCancellation;

            var result = await _sut.ExecuteCommandAsync("sleep 30", TimeSpan.FromMilliseconds(300)).WaitAsync(TimeSpan.FromSeconds(10));

            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Be("Command timed out after 0.3 seconds");
        }

        [Fact]
        public async Task ExecuteCommandAsync_Streamed_CommandNeverCompletes_ReturnsTimedOutFailureWithOutputSoFar()
        {
            _factory.Behaviour = FakeBehaviour.RunsUntilCancelled;
            _factory.OutputBeforeHang = "pulling layer 1\n";
            var lines = new ConcurrentQueue<string>();

            var result = await _sut.ExecuteCommandAsync("docker compose pull", TimeSpan.FromMilliseconds(300), null, lines.Enqueue)
                .WaitAsync(TimeSpan.FromSeconds(10));

            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Be("Command timed out after 0.3 seconds");
            result.Output.Should().Contain("pulling layer 1");
            lines.Should().Equal("pulling layer 1");
            _journal.Should().StartWith("cancel: docker compose pull");
        }

        [Fact]
        public async Task ExecuteCommandAsync_CommandCompletes_ReturnsItsResultWithoutCancelling()
        {
            _factory.Behaviour = FakeBehaviour.CompletesImmediately;

            var result = await _sut.ExecuteCommandAsync("echo hello", TimeSpan.FromMilliseconds(300));
            await Task.Delay(500); // past the timeout: nothing may fire for a finished command

            result.IsSuccess.Should().BeTrue();
            result.Output.Should().Be("hello\n");
            _journal.Should().Equal("handle disposed: echo hello");
        }

        [Fact]
        public async Task Dispose_CommandInFlight_CancelsItBeforeDisposingTheClients()
        {
            _factory.Behaviour = FakeBehaviour.RunsUntilCancelled;
            var execution = _sut.ExecuteCommandAsync("sleep 30", TimeSpan.FromMinutes(10));
            await _factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            _sut.Dispose();

            _journal.Should().Equal("cancel: sleep 30", "handle disposed: sleep 30", "clients disposed");
            execution.IsCompleted.Should().BeTrue("Dispose waits for the cancelled command before releasing the connections");
            var result = await execution;
            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Be("Command was cancelled because the SSH service was disposed while it was running");
        }

        // A Blazor circuit is a single-threaded synchronization context, and Dispose blocks that thread while it waits for the
        // commands it cancelled. SSH.NET completes a command on its session thread, so every continuation the drain needs must not
        // be queued behind the blocked context thread.

        [Fact]
        public void Dispose_OnASingleThreadedContext_CancelAcknowledgedOnSessionThread_DoesNotStarveTheDrain()
        {
            _factory.Behaviour = FakeBehaviour.CancelAcknowledgedOnSessionThread;
            TimeSpan disposeTook = default;
            Task<SshCommandResult>? execution = null;

            SingleThreadSynchronizationContext.Run(async () =>
            {
                execution = _sut.ExecuteCommandAsync("sleep 30", TimeSpan.FromMinutes(10));
                await _factory.Started.Task;
                var sw = Stopwatch.StartNew();
                _sut.Dispose();
                disposeTook = sw.Elapsed;
            });

            disposeTook.Should().BeLessThan(TimeSpan.FromSeconds(2), "the drain must not wait for continuations queued behind the blocked context");
            _journal.Should().Equal("cancel: sleep 30", "handle disposed: sleep 30", "clients disposed");
            execution!.IsCompleted.Should().BeTrue();
        }

        [Fact]
        public void Dispose_OnASingleThreadedContext_TimeoutAlreadyElapsedWhenAwaited_DoesNotStarveTheDrain()
        {
            // The token is cancelled before the command is awaited, so the cancellation path starts synchronously on the context
            // thread and the waits for the session thread's acknowledgement run from there.
            _factory.Behaviour = FakeBehaviour.CancelAcknowledgedOnSessionThread;
            _factory.ExecuteDelay = TimeSpan.FromMilliseconds(100);
            TimeSpan disposeTook = default;
            Task<SshCommandResult>? execution = null;

            SingleThreadSynchronizationContext.Run(async () =>
            {
                execution = _sut.ExecuteCommandAsync("sleep 30", TimeSpan.FromMilliseconds(1));
                await _factory.Started.Task;
                var sw = Stopwatch.StartNew();
                _sut.Dispose();
                disposeTook = sw.Elapsed;
            });

            disposeTook.Should().BeLessThan(TimeSpan.FromSeconds(2), "the drain must not wait for continuations queued behind the blocked context");
            _journal.Should().Equal("cancel: sleep 30", "handle disposed: sleep 30", "clients disposed");
            execution!.Result.Error.Should().Be("Command timed out after 0.001 seconds");
        }

        [Fact]
        public void Dispose_OnASingleThreadedContext_CommandCompletedOnSessionThreadJustBefore_DoesNotStarveTheDrain()
        {
            _factory.Behaviour = FakeBehaviour.CompletedByTest;
            TimeSpan disposeTook = default;
            Task<SshCommandResult>? execution = null;

            SingleThreadSynchronizationContext.Run(async () =>
            {
                execution = _sut.ExecuteCommandAsync("echo hello", TimeSpan.FromMinutes(10));
                await _factory.Started.Task;

                // The command finishes on the session thread; give its continuation time to be scheduled (inline on the pool, or
                // posted to this context) before this thread blocks in Dispose without pumping.
                Task.Run(() => _factory.CompleteLast()).Wait();
                Thread.Sleep(200);

                var sw = Stopwatch.StartNew();
                _sut.Dispose();
                disposeTook = sw.Elapsed;
            });

            disposeTook.Should().BeLessThan(TimeSpan.FromSeconds(2), "the drain must not wait for continuations queued behind the blocked context");
            _journal.Should().Equal("handle disposed: echo hello", "clients disposed");
            execution!.Result.IsSuccess.Should().BeTrue();
        }

        [Fact]
        public async Task ExecuteCommandAsync_CommandCompletesAsTheTimeoutFires_ReturnsTheCommandsResult()
        {
            _factory.Behaviour = FakeBehaviour.CompletesWhenCancelled;

            var result = await _sut.ExecuteCommandAsync("echo hello", TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(10));

            result.IsSuccess.Should().BeTrue();
            result.Output.Should().Be("hello\n");
        }

        [Fact]
        public async Task ExecuteCommandAsync_CommandKilledBySignal_IsAFailure()
        {
            _factory.Behaviour = FakeBehaviour.KilledBySignal;

            var result = await _sut.ExecuteCommandAsync("docker compose up", TimeSpan.FromSeconds(5));

            result.IsSuccess.Should().BeFalse();
            result.ExitCode.Should().Be(-1);
            result.Error.Should().Contain("terminated by signal KILL");
        }

        [Fact]
        public async Task Dispose_CommandIgnoresCancellation_ReleasesClientsAfterBoundedWait()
        {
            _factory.Behaviour = FakeBehaviour.IgnoresCancellation;
            _sut.DisposeDrainTimeout = TimeSpan.FromMilliseconds(300);
            _sut.CancelGrace = TimeSpan.FromSeconds(30);
            _ = _sut.ExecuteCommandAsync("sleep 30", TimeSpan.FromMinutes(10));
            await _factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var sw = Stopwatch.StartNew();

            _sut.Dispose();

            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            _journal.Should().StartWith("cancel: sleep 30").And.EndWith("clients disposed");
        }

        [Fact]
        public async Task ExecuteCommandAsync_AfterDispose_FailsWithoutCreatingACommand()
        {
            _sut.Dispose();

            var result = await _sut.ExecuteCommandAsync("echo hello", TimeSpan.FromSeconds(1));

            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Be("SSH service is disposed");
            _factory.Created.Should().Be(0);
        }

        internal enum FakeBehaviour { CompletesImmediately, RunsUntilCancelled, IgnoresCancellation, CompletesWhenCancelled, KilledBySignal, CancelAcknowledgedOnSessionThread, CompletedByTest }

        internal sealed class FakeCommandFactory(ConcurrentQueue<string> journal) : ISshCommandFactory
        {
            public FakeBehaviour Behaviour { get; set; }
            public string OutputBeforeHang { get; set; } = string.Empty;
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TimeSpan ExecuteDelay { get; set; }
            public int Created;

            public ISshCommandHandle Create(string commandText)
            {
                Interlocked.Increment(ref Created);
                var command = new FakeCommand(commandText, this, journal);
                Last = command;
                return command;
            }

            private FakeCommand? Last;

            public void CompleteLast() => Last!.CompleteSuccessfully();
        }

        /// <summary>Models SSH.NET: cancelling the token signals the remote process, and the task ends cancelled.</summary>
        private sealed class FakeCommand(string text, FakeCommandFactory factory, ConcurrentQueue<string> journal) : ISshCommandHandle
        {
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly FakeOutputStream _output = new(Encoding.UTF8.GetBytes(factory.OutputBeforeHang));

            public Task ExecuteAsync(CancellationToken cancellationToken)
            {
                if (factory.ExecuteDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(factory.ExecuteDelay); // e.g. the channel open round trip, during which a short timeout elapses
                }

                factory.Started.TrySetResult();
                switch (factory.Behaviour)
                {
                    case FakeBehaviour.CompletesImmediately:
                        ExitStatus = 0;
                        _output.End();
                        _completion.SetResult();
                        break;
                    case FakeBehaviour.RunsUntilCancelled:
                        cancellationToken.Register(() =>
                        {
                            journal.Enqueue($"cancel: {text}");
                            _output.End();
                            _completion.TrySetCanceled(cancellationToken);
                        });
                        break;
                    case FakeBehaviour.IgnoresCancellation:
                        cancellationToken.Register(() => journal.Enqueue($"cancel: {text}"));
                        break;
                    case FakeBehaviour.CompletesWhenCancelled:
                        // Registered before SshService's WaitAsync registration, so it runs first: the command has completed
                        // successfully by the time WaitAsync reports the cancellation.
                        cancellationToken.Register(() =>
                        {
                            ExitStatus = 0;
                            _output.End();
                            _completion.TrySetResult();
                        });
                        break;
                    case FakeBehaviour.CancelAcknowledgedOnSessionThread:
                        cancellationToken.Register(() =>
                        {
                            journal.Enqueue($"cancel: {text}");
                            // The server's reply arrives later, on SSH.NET's session thread.
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(50);
                                _output.End();
                                _completion.TrySetCanceled(cancellationToken);
                            });
                        });
                        break;
                    case FakeBehaviour.CompletedByTest:
                        break;
                    case FakeBehaviour.KilledBySignal:
                        ExitSignal = "KILL";
                        _output.End();
                        _completion.SetResult();
                        break;
                }

                return _completion.Task;
            }

            public void CompleteSuccessfully()
            {
                ExitStatus = 0;
                _output.End();
                _completion.TrySetResult();
            }

            public Stream OutputStream => _output;
            public string Result => factory.Behaviour is FakeBehaviour.CompletesImmediately or FakeBehaviour.CompletesWhenCancelled or FakeBehaviour.CompletedByTest ? "hello\n" : string.Empty;
            public string Error => string.Empty;
            public int? ExitStatus { get; private set; }
            public string? ExitSignal { get; private set; }

            public void Dispose() => journal.Enqueue($"handle disposed: {text}");
        }

        /// <summary>Yields the given bytes, then blocks until ended (channel closed) or disposed.</summary>
        private sealed class FakeOutputStream(byte[] initial) : Stream
        {
            private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _position;

            public void End() => _ended.TrySetResult();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position < initial.Length)
                {
                    var n = Math.Min(buffer.Length, initial.Length - _position);
                    initial.AsMemory(_position, n).CopyTo(buffer);
                    _position += n;
                    return n;
                }

                await _ended.Task.WaitAsync(cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

            protected override void Dispose(bool disposing)
            {
                End();
                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>One thread runs every continuation, like a Blazor circuit's renderer context.</summary>
    internal sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public static void Run(Func<Task> body)
        {
            var previous = Current;
            var context = new SingleThreadSynchronizationContext();
            SetSynchronizationContext(context);
            try
            {
                var task = body();
                task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
                foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
                {
                    callback(state);
                }

                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
