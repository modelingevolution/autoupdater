using ModelingEvolution.AutoUpdater.Services;
using System;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Extensions
{
    /// <summary>
    /// Extension methods for SSH operations
    /// </summary>
    public static class SshExtensions
    {
        /// <summary>
        /// Executes a single SSH command and returns the output
        /// </summary>
        /// <param name="connectionManager">The SSH connection manager</param>
        /// <param name="command">The command to execute</param>
        /// <param name="workingDirectory">Optional working directory</param>
        /// <param name="onExecuted">Optional callback when command completes</param>
        /// <returns>The command output</returns>
        public static async Task<string> ExecuteCommandAsync(
            this ISshConnectionManager connectionManager, 
            string command, 
            string? workingDirectory = null, 
            Func<SshCommandResult, Task>? onExecuted = null)
        {
            using var sshService = await connectionManager.CreateSshServiceAsync();
            var result = await sshService.ExecuteCommandAsync(command, workingDirectory);
            
            if (onExecuted != null)
                await onExecuted.Invoke(result);
            
            return result.Output;
        }
    }
}