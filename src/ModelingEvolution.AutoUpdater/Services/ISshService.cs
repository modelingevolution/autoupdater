using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Service for SSH command execution and file operations
    /// </summary>
    public interface ISshService
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
        Task<string> GetArchitectureAsync();
    }

    /// <summary>
    /// Result of an SSH command execution
    /// </summary>
    public record SshCommandResult(
        bool IsSuccess,
        int ExitCode,
        string Output,
        string Error
    );
}