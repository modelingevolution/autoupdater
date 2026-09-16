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

        internal enum FakeBehaviour { CompletesImmediately, RunsUntilCancelled, IgnoresCancellation }

        internal sealed class FakeCommandFactory(ConcurrentQueue<string> journal) : ISshCommandFactory
        {
            public FakeBehaviour Behaviour { get; set; }
            public string OutputBeforeHang { get; set; } = string.Empty;
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Created;

            public ISshCommandHandle Create(string commandText)
            {
                Interlocked.Increment(ref Created);
                return new FakeCommand(commandText, this, journal);
            }
        }

        /// <summary>Models SSH.NET: cancelling the token signals the remote process, and the task ends cancelled.</summary>
        private sealed class FakeCommand(string text, FakeCommandFactory factory, ConcurrentQueue<string> journal) : ISshCommandHandle
        {
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly FakeOutputStream _output = new(Encoding.UTF8.GetBytes(factory.OutputBeforeHang));

            public Task ExecuteAsync(CancellationToken cancellationToken)
            {
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
                }

                return _completion.Task;
            }

            public Stream OutputStream => _output;
            public string Result => factory.Behaviour == FakeBehaviour.CompletesImmediately ? "hello\n" : string.Empty;
            public string Error => string.Empty;
            public int? ExitStatus { get; private set; }

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
}
