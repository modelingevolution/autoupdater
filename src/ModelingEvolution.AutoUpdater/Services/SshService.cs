using Microsoft.Extensions.Logging;
using Renci.SshNet;
using System;
using System.IO;
using System.Net.Mail;
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
            try
            {
                var fullCommand = workingDirectory != null ? $"cd {workingDirectory} && {command}" : command;
                
                _logger.LogDebug("Executing SSH command: {Command}", command);
                
                using var sshCommand = _sshClient.CreateCommand(fullCommand);
                
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
                using var writer = new StreamWriter(memoryStream);
                await writer.WriteAsync(content);
                await writer.FlushAsync();
                
                memoryStream.Position = 0;
                await Task.Run(() => _scpClient.Upload(memoryStream, filePath));
                
                _logger.LogDebug("Successfully wrote file: {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write file: {FilePath}", filePath);
                throw new InvalidOperationException($"Failed to write file {filePath}: {ex.Message}", ex);
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
            if (!_disposed)
            {
                _sshClient?.Dispose();
                _scpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}