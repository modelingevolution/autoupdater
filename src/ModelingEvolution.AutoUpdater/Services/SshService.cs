using Microsoft.Extensions.Logging;
using Renci.SshNet;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// SSH service implementation using SshClient and ScpClient directly
    /// </summary>
    public class SshService : ISshService, IDisposable
    {
        private readonly ISshCommandFactory _commands;
        private readonly ScpClient? _scpClientOrNull;
        private readonly SftpClient? _sftpClientOrNull;
        private readonly Action _disposeClients;
        private readonly ILogger<SshService> _logger;

        // Cancelled by Dispose: every running command is linked to it (bug-019).
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _gate = new();
        private readonly HashSet<Task> _inFlight = new();
        private bool _disposed;

        public SshService(SshClient sshClient, ScpClient scpClient, SftpClient sftpClient, ILogger<SshService> logger)
            : this(
                new SshNetCommandFactory(sshClient ?? throw new ArgumentNullException(nameof(sshClient))),
                scpClient ?? throw new ArgumentNullException(nameof(scpClient)),
                sftpClient ?? throw new ArgumentNullException(nameof(sftpClient)),
                () =>
                {
                    sshClient.Dispose();
                    scpClient.Dispose();
                    sftpClient.Dispose();
                },
                logger)
        {
        }

        /// <summary>
        /// Test seam: commands come from <paramref name="commands"/>, <paramref name="disposeClients"/> releases the connections.
        /// File operations need the SCP/SFTP clients and throw when they are not given.
        /// </summary>
        internal SshService(ISshCommandFactory commands, ScpClient? scpClient, SftpClient? sftpClient, Action disposeClients, ILogger<SshService> logger)
        {
            _commands = commands ?? throw new ArgumentNullException(nameof(commands));
            _scpClientOrNull = scpClient;
            _sftpClientOrNull = sftpClient;
            _disposeClients = disposeClients ?? throw new ArgumentNullException(nameof(disposeClients));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private ScpClient Scp => _scpClientOrNull ?? throw new InvalidOperationException("SCP client is not available");

        private SftpClient Sftp => _sftpClientOrNull ?? throw new InvalidOperationException("SFTP client is not available");

        /// <summary>
        /// How long to wait for the output pump to see end-of-stream after the command itself has returned,
        /// before the stream is closed underneath it.
        /// </summary>
        internal TimeSpan OutputDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// After a timeout or dispose cancelled a command, how long the command gets to finish before its handle is released.
        /// </summary>
        internal TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long <see cref="Dispose"/> waits for cancelled commands to finish before it releases the connections.
        /// </summary>
        internal TimeSpan DisposeDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

        public Task<SshCommandResult> ExecuteCommandAsync(string command, string? workingDirectory = null)
        {
            return ExecuteCommandAsync(command, TimeSpan.FromMinutes(10), workingDirectory);
        }

        public Task<SshCommandResult> ExecuteCommandAsync(string command, TimeSpan timeout, string? workingDirectory = null)
        {
            return RunTrackedAsync(command, timeout, workingDirectory, onOutputLine: null);
        }

        public Task<SshCommandResult> ExecuteCommandAsync(string command, TimeSpan timeout, string? workingDirectory, Action<string> onOutputLine)
        {
            ArgumentNullException.ThrowIfNull(onOutputLine);
            return RunTrackedAsync(command, timeout, workingDirectory, onOutputLine);
        }

        /// <summary>
        /// Registers the command as in flight so <see cref="Dispose"/> cancels it and waits for it before releasing the connections.
        /// </summary>
        private async Task<SshCommandResult> RunTrackedAsync(string command, TimeSpan timeout, string? workingDirectory, Action<string>? onOutputLine)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_disposed)
                {
                    _logger.LogError("SSH command rejected, the SSH service is disposed: {Command}", command);
                    return SshCommandResult.Failed(command, -1, string.Empty, "SSH service is disposed");
                }

                _inFlight.Add(done.Task);
            }

            try
            {
                return await RunAsync(command, timeout, workingDirectory, onOutputLine);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight.Remove(done.Task);
                }

                done.TrySetResult();
            }
        }

        /// <summary>
        /// Runs one command. The timeout is a token (<see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>) linked to this
        /// service's lifetime; <c>SshCommand.CommandTimeout</c> is never used (bug-019).
        /// </summary>
        private async Task<SshCommandResult> RunAsync(string command, TimeSpan timeout, string? workingDirectory, Action<string>? onOutputLine)
        {
            var fullCommand = workingDirectory != null ? $"cd {workingDirectory} && {command}" : command;
            var output = onOutputLine != null ? new StringBuilder() : null;
            var kind = onOutputLine != null ? "Streamed SSH command" : "SSH command";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cts.CancelAfter(timeout);
            ISshCommandHandle? sshCommand = null;

            try
            {
                _logger.LogDebug("Executing {Kind} with timeout {Timeout}: {Command}", kind, timeout, command);

                sshCommand = _commands.Create(fullCommand);
                var execution = sshCommand.ExecuteAsync(cts.Token);
                var pump = onOutputLine != null
                    ? PumpOutputAsync(sshCommand.OutputStream, output!, onOutputLine, command)
                    : null;

                try
                {
                    // WaitAsync: the call returns at the timeout even if the command does not honour the token.
                    await execution.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    await SettleAsync(execution, command);
                    throw;
                }
                finally
                {
                    if (pump != null)
                    {
                        // The stream ends when the channel closes. If that does not happen in time, close it ourselves
                        // so the pump cannot outlive this call, then wait for the pump - it never throws.
                        if (await Task.WhenAny(pump, Task.Delay(OutputDrainTimeout)) != pump)
                        {
                            _logger.LogWarning("Output of SSH command did not reach end-of-stream within {Drain}; closing it: {Command}", OutputDrainTimeout, command);
                            sshCommand.OutputStream.Dispose();
                        }
                        await pump;
                    }
                }

                var commandResult = new SshCommandResult
                {
                    Command = command,
                    ExitCode = sshCommand.ExitStatus ?? 0,
                    Output = output != null ? SnapshotOutput(output) : sshCommand.Result,
                    Error = sshCommand.Error
                };

                if (commandResult.IsSuccess)
                {
                    _logger.LogDebug("{Kind} completed successfully: {Command}", kind, command);
                }
                else
                {
                    _logger.LogWarning("{Kind} failed with exit code {ExitCode}: {Command}. Error: {Error}",
                        kind, commandResult.ExitCode, command, commandResult.Error);
                }

                return commandResult;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                var reason = _lifetime.IsCancellationRequested
                    ? "was cancelled because the SSH service was disposed while it was running"
                    : $"timed out after {timeout.TotalSeconds:0.###} seconds";
                _logger.LogError("{Kind} {Reason}: {Command}", kind, reason, command);
                return SshCommandResult.Failed(command, -1, output != null ? SnapshotOutput(output) : string.Empty, $"Command {reason}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute {Kind}: {Command}", kind, command);
                return SshCommandResult.Failed(command, -1, output != null ? SnapshotOutput(output) : string.Empty, ex.Message);
            }
            finally
            {
                sshCommand?.Dispose();
            }
        }

        /// <summary>
        /// Gives a cancelled command <see cref="CancelGrace"/> to finish, and observes its outcome so it cannot surface later.
        /// </summary>
        private async Task SettleAsync(Task execution, string command)
        {
            if (await Task.WhenAny(execution, Task.Delay(CancelGrace)) != execution)
            {
                _logger.LogWarning("SSH command did not stop within {Grace} after cancellation: {Command}", CancelGrace, command);
            }

            _ = execution.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static string SnapshotOutput(StringBuilder output)
        {
            lock (output)
            {
                return output.ToString();
            }
        }

        private async Task PumpOutputAsync(Stream stream, StringBuilder output, Action<string> onOutputLine, string command)
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                while (await reader.ReadLineAsync() is { } line)
                {
                    lock (output)
                    {
                        output.AppendLine(line);
                    }

                    try
                    {
                        onOutputLine(line);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Output line handler failed for command {Command}", command);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Stream closed underneath us - nothing more to read.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reading streamed output failed for command {Command}", command);
            }
        }

        public async Task<string> ReadFileAsync(string filePath)
        {
            try
            {
                _logger.LogDebug("Reading file via SCP: {FilePath}", filePath);
                
                using var memoryStream = new MemoryStream();
                await Task.Run(() => Scp.Download(filePath, memoryStream));
                
                memoryStream.Position = 0;
                using var reader = new StreamReader(memoryStream);
                var content = await reader.ReadToEndAsync();
                
                _logger.LogDebug("Successfully read file: {FilePath}", filePath);
                return content;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read file: {FilePath}", filePath);
                throw new InvalidOperationException($"Failed to read file {filePath}: {ex.Message}", ex);
            }
        }

        public async Task WriteFileAsync(string filePath, string content)
        {
            try
            {
                _logger.LogDebug("Writing file via SCP: {FilePath}", filePath);
                
                using var memoryStream = new MemoryStream();
                await using var writer = new StreamWriter(memoryStream);
                await writer.WriteAsync(content);
                await writer.FlushAsync();
                
                memoryStream.Position = 0;
                
                // Check if we can write directly to the file
                var canWriteDirectly = await CanWriteToFileAsync(filePath);
                
                if (canWriteDirectly)
                {
                    // Direct write using SCP
                    await Task.Run(() => Scp.Upload(memoryStream, filePath));
                    _logger.LogDebug("Successfully wrote file directly: {FilePath}", filePath);
                }
                else
                {
                    // Write to temp file first, then move with sudo to preserve permissions
                    var tempFilePath = $"/tmp/{Path.GetFileName(filePath)}.{Guid.NewGuid():N}";
                    
                    try
                    {
                        // Upload to temp location
                        await Task.Run(() => Scp.Upload(memoryStream, tempFilePath));
                        
                        // Get original file permissions and ownership if file exists
                        string? originalPermissions = null;
                        string? originalOwnership = null;
                        if (await Sftp.ExistsAsync(filePath))
                        {
                            var statResult = await ExecuteCommandAsync($"stat -c '%a:%U:%G' {filePath}");
                            if (statResult.IsSuccess)
                            {
                                var parts = statResult.Output.Trim().Split(':');
                                if (parts.Length >= 3)
                                {
                                    originalPermissions = parts[0];
                                    originalOwnership = $"{parts[1]}:{parts[2]}";
                                }
                            }
                        }
                        
                        // Set permissions on temp file before moving
                        if (!string.IsNullOrEmpty(originalPermissions)) 
                            await ExecuteCommandAsync($"chmod {originalPermissions} {tempFilePath}");
                        
                        // Set ownership on temp file if we have it (requires sudo)
                        if (!string.IsNullOrEmpty(originalOwnership)) 
                            await ExecuteCommandAsync($"sudo chown {originalOwnership} {tempFilePath}");
                        
                        // Move file with sudo (preserves the permissions we just set)
                        var moveResult = await ExecuteCommandAsync($"sudo mv {tempFilePath} {filePath}");
                        if (!moveResult.IsSuccess)
                            throw new InvalidOperationException($"Failed to move file: {moveResult.Error}");
                        
                        _logger.LogDebug("Successfully wrote file via sudo: {FilePath}", filePath);
                    }
                    finally
                    {
                        // Clean up temp file if it still exists
                        await ExecuteCommandAsync($"rm -f {tempFilePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write file: {FilePath}", filePath);
                throw new InvalidOperationException($"Failed to write file {filePath}: {ex.Message}", ex);
            }
        }
        
        private async Task<bool> CanWriteToFileAsync(string filePath)
        {
            try
            {
                // Check if directory exists and is writable
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    var result = await ExecuteCommandAsync($"test -w {directory}");
                    if (!result.IsSuccess)
                    {
                        return false;
                    }
                }
                
                // If file exists, check if it's writable
                if (Sftp.Exists(filePath))
                {
                    var result = await ExecuteCommandAsync($"test -w {filePath}");
                    return result.IsSuccess;
                }
                
                // File doesn't exist, but directory is writable
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking write permissions for {FilePath}, assuming no write access", filePath);
                return false;
            }
        }

        public async Task MakeExecutableAsync(string filePath)
        {
            var command = $"chmod +x {filePath}";
            var result = await ExecuteCommandAsync(command);
            
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Failed to make file executable {filePath}: {result.Error}");
            }
        }

        public async Task<CpuArchitecture> GetArchitectureAsync()
        {
            var result = await ExecuteCommandAsync("uname -m");
            
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Failed to get architecture: {result.Error}");
            }
            
            // Map common architecture names to standardized values
            var arch = result.Output.Trim().ToLowerInvariant();
            return arch switch
            {
                "x86_64" or "amd64" => CpuArchitecture.X64,
                "aarch64" or "arm64" => CpuArchitecture.Arm64,
                "armv7l" or "arm" => CpuArchitecture.Arm,
                _ => throw new NotSupportedException("Unsupported CPU architecture")
            };
        }

        public async Task<bool> FileExistsAsync(string filePath)
        {
            var command = $"test -f {filePath}";
            var result = await ExecuteCommandAsync(command);
            return result.IsSuccess;
        }

        public async Task<bool> IsExecutableAsync(string filePath)
        {
            var command = $"test -x {filePath}";
            var result = await ExecuteCommandAsync(command);
            return result.IsSuccess;
        }

        public string[] GetFiles(string path, string pattern)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path cannot be null or empty.", nameof(path));

                if (string.IsNullOrWhiteSpace(pattern))
                    throw new ArgumentException("Pattern cannot be null or empty.", nameof(pattern));

                var files = Sftp.ListDirectory(path)
                    .Where(file => file.IsRegularFile && file.Name.Like(pattern))
                    .Select(file => file.FullName)
                    .ToArray();

                return files;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while retrieving files from path '{Path}' with pattern '{Pattern}'.",
                    path, pattern);
                throw;
            }
        }

        public async Task<bool> DirectoryExistsAsync(string directoryPath)
        {
            var command = $"test -d {directoryPath}";
            var result = await ExecuteCommandAsync(command);
            return result.IsSuccess;
        }

        public async Task CreateDirectoryAsync(string directoryPath)
        {
            var command = $"mkdir -p {directoryPath}";
            var result = await ExecuteCommandAsync(command);
            
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Failed to create directory {directoryPath}: {result.Error}");
            }
        }

        /// <summary>
        /// Cancels every command still running, waits (bounded by <see cref="DisposeDrainTimeout"/>) for them to finish, and only
        /// then releases the connections - a connection is never torn down under a running command (bug-019).
        /// </summary>
        public void Dispose()
        {
            Task[] pending;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                pending = _inFlight.ToArray();
            }

            if (pending.Length > 0)
            {
                _logger.LogWarning("SSH service disposed with {Count} command(s) still running; cancelling them", pending.Length);
            }

            try
            {
                _lifetime.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cancelling running SSH commands failed");
            }

            if (pending.Length > 0 && !Task.WhenAll(pending).Wait(DisposeDrainTimeout))
            {
                _logger.LogWarning("SSH commands still running {Drain} after cancellation; releasing the connections anyway", DisposeDrainTimeout);
            }

            _disposeClients();
        }
    }
}