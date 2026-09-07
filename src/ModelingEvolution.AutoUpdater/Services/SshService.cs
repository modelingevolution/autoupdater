using Microsoft.Extensions.Logging;
using Renci.SshNet;
using System;
using System.IO;
using System.Net.Mail;
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
        private readonly SshClient _sshClient;
        private readonly ScpClient _scpClient;
        
        private readonly SftpClient _sftpClient;
        private readonly ILogger<SshService> _logger;
        private bool _disposed;

        public SshService(SshClient sshClient, ScpClient scpClient, SftpClient sftpClient, ILogger<SshService> logger)
        {
            _sshClient = sshClient ?? throw new ArgumentNullException(nameof(sshClient));
            _scpClient = scpClient ?? throw new ArgumentNullException(nameof(scpClient));
            _sftpClient = sftpClient ?? throw new ArgumentNullException(nameof(sftpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<SshCommandResult> ExecuteCommandAsync(string command, string? workingDirectory = null)
        {
            return await ExecuteCommandAsync(command, TimeSpan.FromMinutes(10), workingDirectory);
        }

        public async Task<SshCommandResult> ExecuteCommandAsync(string command, TimeSpan timeout, string? workingDirectory = null)
        {
            try
            {
                var fullCommand = workingDirectory != null ? $"cd {workingDirectory} && {command}" : command;
                
                _logger.LogDebug("Executing SSH command with timeout {Timeout}: {Command}", timeout, command);
                
                using var sshCommand = _sshClient.CreateCommand(fullCommand);
                sshCommand.CommandTimeout = timeout;
                
                await sshCommand.ExecuteAsync();
                
                var commandResult = new SshCommandResult
                {
                    Command = command,
                    ExitCode = sshCommand.ExitStatus ?? 0,
                    Output = sshCommand.Result,
                    Error = sshCommand.Error
                };

                if (sshCommand.ExitStatus == 0)
                {
                    _logger.LogDebug("SSH command completed successfully: {Command}", command);
                }
                else
                {
                    _logger.LogWarning("SSH command failed with exit code {ExitCode}: {Command}. Error: {Error}", 
                        sshCommand.ExitStatus, command, sshCommand.Error);
                }

                return commandResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute SSH command: {Command}", command);
                return SshCommandResult.Failed(command, -1, string.Empty, ex.Message);
            }
        }

        /// <summary>
        /// How long to wait for the output pump to see end-of-stream after the command itself has returned,
        /// before the stream is closed underneath it.
        /// </summary>
        internal TimeSpan OutputDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public async Task<SshCommandResult> ExecuteCommandAsync(string command, TimeSpan timeout, string? workingDirectory, Action<string> onOutputLine)
        {
            ArgumentNullException.ThrowIfNull(onOutputLine);

            var fullCommand = workingDirectory != null ? $"cd {workingDirectory} && {command}" : command;
            var output = new StringBuilder();

            try
            {
                _logger.LogDebug("Executing streamed SSH command with timeout {Timeout}: {Command}", timeout, command);

                using var sshCommand = _sshClient.CreateCommand(fullCommand);
                sshCommand.CommandTimeout = timeout;

                using var cts = new CancellationTokenSource(timeout);
                var execution = sshCommand.ExecuteAsync(cts.Token);
                var pump = PumpOutputAsync(sshCommand.OutputStream, output, onOutputLine, command);

                try
                {
                    await execution;
                }
                finally
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

                var commandResult = new SshCommandResult
                {
                    Command = command,
                    ExitCode = sshCommand.ExitStatus ?? 0,
                    Output = SnapshotOutput(output),
                    Error = sshCommand.Error
                };

                if (commandResult.IsSuccess)
                {
                    _logger.LogDebug("Streamed SSH command completed successfully: {Command}", command);
                }
                else
                {
                    _logger.LogWarning("Streamed SSH command failed with exit code {ExitCode}: {Command}. Error: {Error}",
                        commandResult.ExitCode, command, commandResult.Error);
                }

                return commandResult;
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("Streamed SSH command timed out after {Timeout}: {Command}", timeout, command);
                return SshCommandResult.Failed(command, -1, SnapshotOutput(output), $"Command timed out after {timeout}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute streamed SSH command: {Command}", command);
                return SshCommandResult.Failed(command, -1, SnapshotOutput(output), ex.Message);
            }
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
                await Task.Run(() => _scpClient.Download(filePath, memoryStream));
                
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
                    await Task.Run(() => _scpClient.Upload(memoryStream, filePath));
                    _logger.LogDebug("Successfully wrote file directly: {FilePath}", filePath);
                }
                else
                {
                    // Write to temp file first, then move with sudo to preserve permissions
                    var tempFilePath = $"/tmp/{Path.GetFileName(filePath)}.{Guid.NewGuid():N}";
                    
                    try
                    {
                        // Upload to temp location
                        await Task.Run(() => _scpClient.Upload(memoryStream, tempFilePath));
                        
                        // Get original file permissions and ownership if file exists
                        string? originalPermissions = null;
                        string? originalOwnership = null;
                        if (await _sftpClient.ExistsAsync(filePath))
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
                if (_sftpClient.Exists(filePath))
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

                var files = _sftpClient.ListDirectory(path)
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

        public void Dispose()
        {
            if (_disposed) return;
            
            _sshClient?.Dispose();
            _scpClient?.Dispose();
            _sftpClient?.Dispose();
            _disposed = true;
        }
    }
}