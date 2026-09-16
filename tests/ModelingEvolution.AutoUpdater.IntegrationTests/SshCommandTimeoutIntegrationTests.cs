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
        // Ryuk removes these if the test host dies (which is exactly what a bad SSH.NET version does here). With
        // TESTCONTAINERS_RYUK_DISABLED=true, clean up by label:
        //   docker rm -f $(docker ps -aq --filter label=com.modelingevolution.test=bug-019)
        .WithLabel("com.modelingevolution.test", "bug-019")
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

        // The deploy user on a device has passwordless sudo; WriteFileAsync relies on it for files the user cannot write.
        await ExecAsync("sh", "-c", $"echo '{User} ALL=(ALL) NOPASSWD: ALL' > /etc/sudoers.d/bug019 && chmod 440 /etc/sudoers.d/bug019");

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

    /// <summary>Runs a command in the sshd container as root.</summary>
    public async Task<string> ExecAsync(params string[] command)
    {
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0 && command[0] != "pgrep")
        {
            throw new InvalidOperationException($"'{string.Join(' ', command)}' exited {result.ExitCode}: {result.Stderr}");
        }

        return result.Stdout;
    }

    /// <summary>Remote processes whose command line contains <paramref name="pattern"/>, polled until none or the deadline.</summary>
    public async Task<string> WaitForNoProcessAsync(string pattern, TimeSpan deadline)
    {
        var sw = Stopwatch.StartNew();
        string found;
        do
        {
            found = (await ExecAsync("pgrep", "-af", pattern)).Trim();
            if (found.Length == 0) return found;
            await Task.Delay(200);
        }
        while (sw.Elapsed < deadline);

        return found;
    }

    public SshService CreateService(ILoggerFactory loggerFactory) => new(
        Connected(new SshClient(Host, Port, User, Password)),
        Connected(new ScpClient(Host, Port, User, Password)),
        Connected(new SftpClient(Host, Port, User, Password)),
        loggerFactory.CreateLogger<SshService>());

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

        var execution = service.ExecuteCommandAsync("sleep 32", TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(1));
        service.Dispose();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));
        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] result: exit={result.ExitCode} error='{result.Error}'");
        Assert.False(result.IsSuccess);
        Assert.Contains("cancelled", result.Error);
        var remote = await _sshd.WaitForNoProcessAsync("sleep 32", TimeSpan.FromSeconds(3));
        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] remote 'sleep 32' after dispose: '{remote}'");
        Assert.Equal(string.Empty, remote);

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

        var result = await service.ExecuteCommandAsync("sleep 31", TimeSpan.FromSeconds(3)).WaitAsync(TimeSpan.FromSeconds(15));

        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] result: exit={result.ExitCode} error='{result.Error}'");
        Assert.False(result.IsSuccess);
        Assert.Equal("Command timed out after 3 seconds", result.Error);
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(10));
        var remote = await _sshd.WaitForNoProcessAsync("sleep 31", TimeSpan.FromSeconds(3));
        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] remote 'sleep 31' after timeout: '{remote}'");
        Assert.Equal(string.Empty, remote);

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
        var command = client.CreateCommand("sleep 33");
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

    /// <summary>
    /// A timed-out command run in a working directory is a shell (<c>cd … &amp;&amp; cmd</c>): records whether the signal reaches the child.
    /// </summary>
    [Fact]
    public async Task SshService_TimeoutInWorkingDirectory_RemoteChildProcessIsStopped()
    {
        using var service = _sshd.CreateService(_loggerFactory);

        var result = await service.ExecuteCommandAsync("sleep 34", TimeSpan.FromSeconds(2), "/tmp").WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("Command timed out after 2 seconds", result.Error);
        var remote = await _sshd.WaitForNoProcessAsync("sleep 34", TimeSpan.FromSeconds(3));
        _output.WriteLine($"remote 'sleep 34' after timeout (cd /tmp && sleep 34): '{remote}'");
        Assert.Equal(string.Empty, remote);
    }

    /// <summary>
    /// The product path of the incident: the connection manager is disposed while a command of a live service is still running.
    /// On SSH.NET 2024.2.0 that killed the process through product code.
    /// </summary>
    [Fact]
    public async Task SshConnectionManager_DisposedWhileAServiceCommandIsRunning_CommandEndsAndProcessStaysAlive()
    {
        var manager = CreateManager();
        using var service = await manager.CreateSshServiceAsync();
        var sw = Stopwatch.StartNew();

        var execution = service.ExecuteCommandAsync("sleep 35", TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromSeconds(1));
        manager.Dispose();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));
        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] result: exit={result.ExitCode} error='{result.Error}'");
        Assert.False(result.IsSuccess);
        Assert.Equal("Command timed out after 3 seconds", result.Error);
        Assert.Equal(string.Empty, await _sshd.WaitForNoProcessAsync("sleep 35", TimeSpan.FromSeconds(3)));

        // Past the timeout with margin: on 2024.2.0 the timer fired here against the client the manager had disposed.
        await Task.Delay(TimeSpan.FromSeconds(5));
        _output.WriteLine($"[{sw.Elapsed.TotalSeconds:0.00}s] test host alive (pid {Environment.ProcessId})");
    }

    [Fact]
    public async Task SshConnectionManager_Dispose_DoesNotDisposeAClientHandedToAnSshService()
    {
        var manager = CreateManager();
        using var service = await manager.CreateSshServiceAsync();

        manager.Dispose();
        var result = await service.ExecuteCommandAsync("echo still-connected", TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("still-connected", result.Output.Trim());
    }

    private SshConnectionManager CreateManager() => new(
            new SshConfiguration
            {
                Host = _sshd.Host,
                Port = _sshd.Port,
                User = SshdFixture.User,
                Password = SshdFixture.Password,
                AuthMethod = SshAuthMethod.Password
            },
            _loggerFactory.CreateLogger<SshConnectionManager>());
}

/// <summary>
/// File operations of <see cref="SshService"/> over SCP / SFTP / sudo against a real sshd, on the SSH.NET version the package ships.
/// These write deployment state on every device.
/// </summary>
public class SshServiceFileOperationsIntegrationTests : IClassFixture<SshdFixture>
{
    private readonly SshdFixture _sshd;
    private readonly ILoggerFactory _loggerFactory;

    public SshServiceFileOperationsIntegrationTests(SshdFixture sshd, ITestOutputHelper output)
    {
        _sshd = sshd;
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output)).SetMinimumLevel(LogLevel.Debug));
    }

    [Fact]
    public async Task WriteFileAsync_WritableDirectory_WritesDirectlyAndReadsBack()
    {
        using var service = _sshd.CreateService(_loggerFactory);
        var dir = $"/config/bug019-{Guid.NewGuid():N}";
        await service.CreateDirectoryAsync(dir);
        var path = $"{dir}/deployment.state.json";
        var content = "{\"version\":\"1.0.82\",\"note\":\"zażółć\"}\n";

        await service.WriteFileAsync(path, content);

        Assert.Equal(content, await service.ReadFileAsync(path));
        Assert.True(await service.FileExistsAsync(path));
        Assert.True(await service.DirectoryExistsAsync(dir));
        Assert.Equal($"{SshdFixture.User}\n", await _sshd.ExecAsync("stat", "-c", "%U", path));
    }

    [Fact]
    public async Task WriteFileAsync_RootOwnedFileInRootOwnedDirectory_GoesThroughSudoMoveAndKeepsOwnerAndMode()
    {
        using var service = _sshd.CreateService(_loggerFactory);
        var dir = $"/opt/bug019-{Guid.NewGuid():N}";
        var path = $"{dir}/docker-compose.override.yml";
        await _sshd.ExecAsync("sh", "-c", $"mkdir -p {dir} && echo old > {path} && chown -R root:root {dir} && chmod 755 {dir} && chmod 640 {path}");

        await service.WriteFileAsync(path, "services: {}\n");

        Assert.Equal("services: {}\n", await _sshd.ExecAsync("cat", path));
        Assert.Equal("640:root:root\n", await _sshd.ExecAsync("stat", "-c", "%a:%U:%G", path));
        Assert.Equal(string.Empty, (await _sshd.ExecAsync("sh", "-c", $"ls /tmp | grep docker-compose.override.yml || true")).Trim());
    }

    [Fact]
    public async Task FileExistsAsync_MissingFile_IsFalse_AndMakeExecutableIsSeen()
    {
        using var service = _sshd.CreateService(_loggerFactory);
        var path = $"/config/bug019-{Guid.NewGuid():N}.sh";

        Assert.False(await service.FileExistsAsync(path));
        await service.WriteFileAsync(path, "#!/bin/sh\necho hi\n");
        Assert.False(await service.IsExecutableAsync(path));
        await service.MakeExecutableAsync(path);

        Assert.True(await service.IsExecutableAsync(path));
    }

    [Fact]
    public async Task GetFiles_SftpListing_ReturnsRegularFilesMatchingThePattern()
    {
        using var service = _sshd.CreateService(_loggerFactory);
        var dir = $"/config/bug019-{Guid.NewGuid():N}";
        await _sshd.ExecAsync("sh", "-c", $"mkdir -p {dir}/sub.yml && touch {dir}/up-1.0.1.sh {dir}/up-1.0.2.sh {dir}/readme.md && chown -R 1000:1000 {dir}");

        var files = service.GetFiles(dir, "up-*.sh");

        Assert.Equal(new[] { $"{dir}/up-1.0.1.sh", $"{dir}/up-1.0.2.sh" }, files.OrderBy(f => f, StringComparer.Ordinal));
    }
}
