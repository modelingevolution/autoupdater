using System;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for SSH command execution and file operations
    /// </summary>
    public interface ISshService : IDisposable
    {
        /// <summary>
        /// Executes a command via SSH
        /// </summary>
        /// <param name="command">The command to execute</param>
        /// <param name="workingDirectory">Optional working directory for the command</param>
        /// <returns>The result of the SSH command execution</returns>
        Task<SshCommandResult> ExecuteCommandAsync(string command, string? workingDirectory = null);


        /// <summary>
        /// Reads the content of a file on the remote host
        /// </summary>
        /// <param name="filePath">The path to the file on the remote host</param>
        /// <returns>The content of the file</returns>
        Task<string> ReadFileAsync(string filePath);

        /// <summary>
        /// Writes content to a file on the remote host
        /// </summary>
        /// <param name="filePath">The path to the file on the remote host</param>
        /// <param name="content">The content to write</param>
        Task WriteFileAsync(string filePath, string content);

        /// <summary>
        /// Makes a file executable on the remote host
        /// </summary>
        /// <param name="filePath">The path to the file to make executable</param>
        Task MakeExecutableAsync(string filePath);

        /// <summary>
        /// Detects the architecture of the remote host
        /// </summary>
        /// <returns>The architecture string (e.g., "x64", "arm64")</returns>
        Task<CpuArchitecture> GetArchitectureAsync();

        /// <summary>
        /// Checks if a file exists on the remote host
        /// </summary>
        /// <param name="filePath">The path to the file to check</param>
        /// <returns>True if the file exists, false otherwise</returns>
        Task<bool> FileExistsAsync(string filePath);

        string[] GetFiles(string path, string pattern);

        /// <summary>
        /// Checks if a file is executable on the remote host
        /// </summary>
        /// <param name="filePath">The path to the file to check</param>
        /// <returns>True if the file is executable, false otherwise</returns>
        Task<bool> IsExecutableAsync(string filePath);

        /// <summary>
        /// Checks if a directory exists on the remote host
        /// </summary>
        /// <param name="directoryPath">The path to the directory to check</param>
        /// <returns>True if the directory exists, false otherwise</returns>
        Task<bool> DirectoryExistsAsync(string directoryPath);

        /// <summary>
        /// Creates a directory on the remote host
        /// </summary>
        /// <param name="directoryPath">The path to the directory to create</param>
        Task CreateDirectoryAsync(string directoryPath);
    }

    
}