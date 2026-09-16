using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;
using ModelingEvolution.AutoUpdater.Services;
using Renci.SshNet;
using Xunit;
using Xunit.Abstractions;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

/// <summary>
/// Throwaway sshd (linuxserver/openssh-server) for the bug-019 tests. Removed on dispose.
/// </summary>
public sealed class SshdFixture : IAsyncLifetime
{
    public const string User = "bug019";
    public const string Password = "bug019";
    private const int SshdPort = 2222;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("lscr.io/linuxserver/openssh-server:latest")
        .WithName($"autoupdater-bug019-sshd-{Guid.NewGuid():N}")
        .WithEnvironment("PUID", "1000")
        .WithEnvironment("PGID", "1000")
        .WithEnvironment("PASSWORD_ACCESS", "true")
        .WithEnvironment("USER_NAME", User)
        .WithEnvironment("USER_PASSWORD", Password)
        .WithPortBinding(SshdPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(SshdPort))
        .Build();

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(SshdPort);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // The port opens before sshd has its keys and user; retry until a login works.
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var probe = new SshClient(Host, Port, User, Password);
                probe.Connect();
                return;
            }
            catch when (deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(500);
            }
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public T Connected<T>(T client) where T : BaseClient
    {
        client.Connect();
        return client;
    }
}

/// <summary>
/// bug-019: a command still running when its SSH connection is released must not crash the process.
/// A crash here kills the test host, so the run aborts instead of reporting a pass.
/// </summary>
public class SshCommandTimeoutIntegrationTests : IClassFixture<SshdFixture>
{
    private readonly SshdFixture _sshd;
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;

    public SshCommandTimeoutIntegrationTests(SshdFixture sshd, ITestOutputHelper output)
    {
        _sshd = sshd;
        _output = output;
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output)).SetMinimumLevel(LogLevel.Debug));
    }

    [Fact]
    public async Task SshService_DisposedMidCommand_CommandEndsAndProcessStaysAlive()
    {
        var service = new SshService(
            _sshd.Connected(new SshClient(_sshd.Host, _sshd.Port, SshdFixture.User, SshdFixture.Password)),
            _sshd.Connected(new ScpClient(_sshd.Host, _sshd.Port, SshdFixture.User, SshdFixture.Password)),
            _sshd.Connected(new SftpClient(_sshd.Host, _sshd.Port, SshdFixture.User, SshdFixture.Password)),
            _loggerFactory.CreateLogger<SshService>());
        var sw = Stopwatch.StartNew();

        var execution = service.ExecuteCommandAsync("sleep 30", TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(1));
        service.Dispose();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));
        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] result: exit={result.ExitCode} error='{result.Error}'");
        Assert.False(result.IsSuccess);
        Assert.Contains("cancelled", result.Error);

        // Past the 5 s timeout with margin: in SSH.NET 2024.2.0 the timer fired here on the disposed client and aborted the process.
        await Task.Delay(TimeSpan.FromSeconds(7));
        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] test host alive (pid {Environment.ProcessId})");
    }

    [Fact]
    public async Task SshService_CommandOutlivesTimeout_ReturnsTimedOutAndProcessStaysAlive()
    {
        using var service = new SshService(
            _sshd.Connected(new SshClient(_sshd.Host, _sshd.Port, SshdFixture.User, SshdFixture.Password)),
            _sshd.Connected(new ScpClient(_sshd.Host, _sshd.Port, SshdFixture.User, SshdFixture.Password)),
            _sshd.Connected(new SftpClient(_sshd.Host, _sshd.Port, SshdFixture.User, SshdFixture.Password)),
            _loggerFactory.CreateLogger<SshService>());
        var sw = Stopwatch.StartNew();

        var result = await service.ExecuteCommandAsync("sleep 30", TimeSpan.FromSeconds(3)).WaitAsync(TimeSpan.FromSeconds(15));

        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] result: exit={result.ExitCode} error='{result.Error}'");
        Assert.False(result.IsSuccess);
        Assert.Equal("Command timed out after 3 seconds", result.Error);
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(10));

        var after = await service.ExecuteCommandAsync("echo alive", TimeSpan.FromSeconds(5));
        Assert.True(after.IsSuccess, after.Error);
        Assert.Equal("alive", after.Output.Trim());
    }

    /// <summary>
    /// Pins the SSH.NET fix itself (incident scenario E2): a token cancels a command whose client this side already disposed.
    /// SSH.NET 2024.2.0 threw from the token callback on a thread-pool thread and aborted the process.
    /// </summary>
    [Fact]
    public async Task SshNet_ClientDisposedThenTokenCancelsRunningCommand_ProcessStaysAlive()
    {
        var client = _sshd.Connected(new SshClient(_sshd.Host, _sshd.Port, SshdFixture.User, SshdFixture.Password));
        var command = client.CreateCommand("sleep 30");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var execution = command.ExecuteAsync(cts.Token);
        var sw = Stopwatch.StartNew();

        await Task.Delay(TimeSpan.FromSeconds(1));
        client.Dispose();

        var outcome = await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(10)));
        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] command task: {(outcome == execution ? execution.Status.ToString() : "still pending")}");
        Assert.Same(execution, outcome);
        _ = execution.Exception;

        await Task.Delay(TimeSpan.FromSeconds(3));
        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] test host alive (pid {Environment.ProcessId})");
    }
}
