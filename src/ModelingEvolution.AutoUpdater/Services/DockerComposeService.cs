using Docker.DotNet;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Implementation of Docker Compose service
    /// </summary>
    public class DockerComposeService : IDockerComposeService
    {
        private readonly ISshService _sshService;
        private readonly ILogger<DockerComposeService> _logger;

        public DockerComposeService(ISshService sshService, ILogger<DockerComposeService> logger)
        {
            _sshService = sshService ?? throw new ArgumentNullException(nameof(sshService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string[]> GetComposeFiles(string directoryPath,
            CpuArchitecture architecture)
        {
            try
            {
                
                _logger.LogDebug("Getting compose files for architecture {Architecture} in {DirectoryPath}", architecture, directoryPath);

                if (string.IsNullOrWhiteSpace(directoryPath))
                    throw new ArgumentException("Directory path cannot be null or whitespace", nameof(directoryPath));

                
                var notValid = CpuArchitecture.All
                    .Where(x => x != architecture)
                    .Select(x=> Path.Combine(directoryPath, $"docker-compose.{x}.yml"))
                    .ToHashSet();

                var composeFiles = _sshService
                    .GetFiles(directoryPath, "docker-compose*yml")
                    .Except(notValid)
                    .OrderBy(x => x.Length)
                    .Select(Path.GetFileName)
                    .ToArray();
                //var composeFiles = Directory.GetFiles(directoryPath, "docker-compose*yml")
                //    .Except(notValid)
                //    .OrderBy(x=>x.Length)
                //    .Select(Path.GetFileName)
                //    .ToArray();

                _logger.LogInformation("Found docker-compose files: {docker-compose-files}.", string.Join(',', composeFiles.Select(Path.GetFileName)));
                
                return composeFiles;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get compose files for architecture {Architecture} in {DirectoryPath}", 
                    architecture, directoryPath);
                throw;
            }
        }

        public async Task StartServicesAsync(string[] composeFiles, string workingDirectory)
        {
            try
            {
                _logger.LogInformation("Starting Docker Compose services with {Count} compose files in {WorkingDirectory}", 
                    composeFiles?.Length, workingDirectory);

                if (composeFiles == null || composeFiles.Length == 0)
                {
                    throw new ArgumentException("At least one compose file must be specified", nameof(composeFiles));
                }

                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    throw new ArgumentException("Working directory cannot be null or whitespace", nameof(workingDirectory));
                }

                // Build the docker-compose command with multiple -f flags
                var composeFileArgs = string.Join(" ", composeFiles.Select(f => $"-f \"{f}\""));
                var command = $"docker-compose {composeFileArgs} up -d";

                _logger.LogDebug("Executing Docker Compose command: {Command}", command);

                var result = await _sshService.ExecuteCommandAsync(command, workingDirectory);

                _logger.LogInformation("Docker Compose services started successfully");
                _logger.LogDebug("Docker Compose output: {Output}", result.Output);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Docker Compose services");
                throw;
            }
        }

        public async Task StopServicesAsync(string[] composeFiles, string workingDirectory)
        {
            try
            {
                _logger.LogInformation("Stopping Docker Compose services with {Count} compose files in {WorkingDirectory}", 
                    composeFiles?.Length, workingDirectory);

                if (composeFiles == null || composeFiles.Length == 0)
                {
                    throw new ArgumentException("At least one compose file must be specified", nameof(composeFiles));
                }

                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    throw new ArgumentException("Working directory cannot be null or whitespace", nameof(workingDirectory));
                }

                // Build the docker-compose command with multiple -f flags
                var composeFileArgs = string.Join(" ", composeFiles.Select(f => $"-f \"{f}\""));
                var command = $"docker-compose {composeFileArgs} down";

                _logger.LogDebug("Executing Docker Compose command: {Command}", command);

                var result = await _sshService.ExecuteCommandAsync(command, workingDirectory);

                _logger.LogInformation("Docker Compose services stopped successfully");
                _logger.LogDebug("Docker Compose output: {Output}", result.Output);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop Docker Compose services");
                throw;
            }
        }

        public async Task StopServicesAsync(string projectName)
        {
            try
            {
                _logger.LogInformation("Stopping Docker Compose project: {ProjectName}", projectName);

                if (string.IsNullOrWhiteSpace(projectName))
                {
                    throw new ArgumentException("Project name cannot be null or whitespace", nameof(projectName));
                }

                var command = $"docker-compose -p \"{projectName}\" down";

                _logger.LogDebug("Executing Docker Compose command: {Command}", command);

                var result = await _sshService.ExecuteCommandAsync(command);

                _logger.LogInformation("Docker Compose project stopped successfully: {ProjectName}", projectName);
                _logger.LogDebug("Docker Compose output: {Output}", result.Output);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop Docker Compose project: {ProjectName}", projectName);
                throw;
            }
        }

        public async Task<ComposeProjectStatus> GetProjectStatusAsync(string projectName)
        {
            try
            {
                _logger.LogDebug("Getting status for Docker Compose project: {ProjectName}", projectName);

                if (string.IsNullOrWhiteSpace(projectName))
                {
                    throw new ArgumentException("Project name cannot be null or whitespace", nameof(projectName));
                }

                var command = $"docker-compose -p \"{projectName}\" ps --format json";

                _logger.LogDebug("Executing Docker Compose command: {Command}", command);

                var result = await _sshService.ExecuteCommandAsync(command);

                if (!result.IsSuccess)
                {
                    _logger.LogWarning("Failed to get project status for {ProjectName}: {Error}", projectName, result.Error);
                    return new ComposeProjectStatus("unknown", new List<string>(), 0, 0);
                }

                // Parse the JSON output to determine project status
                var output = result.Output.Trim();
                var isRunning = !string.IsNullOrEmpty(output) && output != "[]";
                var status = isRunning ? "running" : "stopped";
                var runningServices = isRunning ? 1 : 0; // Simplified - would need JSON parsing for accurate count
                var totalServices = isRunning ? 1 : 0;   // Simplified - would need JSON parsing for accurate count

                _logger.LogDebug("Project {ProjectName} status: {Status}", projectName, status);
                return new ComposeProjectStatus(status, new List<string>(), runningServices, totalServices);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get status for Docker Compose project: {ProjectName}", projectName);
                return new ComposeProjectStatus("error", new List<string>(), 0, 0);
            }
        }

        public async Task PullImagesAsync(string[] composeFiles, string workingDirectory)
        {
            try
            {
                _logger.LogInformation("Pulling Docker images for {Count} compose files in {WorkingDirectory}", 
                    composeFiles?.Length, workingDirectory);

                if (composeFiles == null || composeFiles.Length == 0)
                {
                    throw new ArgumentException("At least one compose file must be specified", nameof(composeFiles));
                }

                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    throw new ArgumentException("Working directory cannot be null or whitespace", nameof(workingDirectory));
                }

                // Build the docker-compose command with multiple -f flags
                var composeFileArgs = string.Join(" ", composeFiles.Select(f => $"-f \"{f}\""));
                var command = $"docker-compose {composeFileArgs} pull";

                _logger.LogDebug("Executing Docker Compose command: {Command}", command);

                var result = await _sshService.ExecuteCommandAsync(command, workingDirectory);

                _logger.LogInformation("Docker images pulled successfully");
                _logger.LogDebug("Docker Compose output: {Output}", result.Output);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pull Docker images");
                throw;
            }
        }

        public async Task<IDictionary<string, string>> GetVolumeMappingsAsync(string containerId)
        {
            using var config = new DockerClientConfiguration();
            using var client = config.CreateClient();
            var container = await client.Containers.InspectContainerAsync(containerId);
            var volumeMappings = new Dictionary<string, string>();
                
            if (container.HostConfig.Binds != null)
            {
                foreach (var bind in container.HostConfig.Binds)
                {
                    var parts = bind.Split(':');
                    if (parts.Length == 2)
                    {
                        var hostPath = parts[0];
                        var containerPath = parts[1];
                        volumeMappings[hostPath] = containerPath;
                    } 
                    else if(parts.Length > 2)
                    {
                        if (parts[0].Length == 1)
                        {
                            // it's most likely windows.
                            string hostPath = parts[0] + ":" + parts[1];
                            var containerPath = parts[2];
                            volumeMappings[hostPath] = containerPath;
                        }
                        else
                        {
                            var hostPath = parts[0];
                            var containerPath = parts[1];
                            volumeMappings[hostPath] = containerPath;
                        }
                    }
                }
            }

            return volumeMappings;
        }

        public async Task<string> GetServicesStatusAsync(string[] composeFiles, string workingDirectory)
        {
            try
            {
                _logger.LogDebug("Getting Docker Compose services status for {Count} compose files in {WorkingDirectory}", 
                    composeFiles?.Length, workingDirectory);

                if (composeFiles == null || composeFiles.Length == 0)
                {
                    throw new ArgumentException("At least one compose file must be specified", nameof(composeFiles));
                }

                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    throw new ArgumentException("Working directory cannot be null or whitespace", nameof(workingDirectory));
                }

                // Build the docker-compose command with multiple -f flags
                var composeFileArgs = string.Join(" ", composeFiles.Select(f => $"-f \"{f}\""));
                var command = $"docker-compose {composeFileArgs} ps";

                _logger.LogDebug("Executing Docker Compose command: {Command}", command);

                var result = await _sshService.ExecuteCommandAsync(command, workingDirectory);

                _logger.LogDebug("Retrieved services status successfully");
                return result.Output;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Docker Compose services status");
                throw;
            }
        }

        public async Task RestartServicesAsync(string[] composeFiles, string workingDirectory, bool nohup=false)
        {
            try
            {
                _logger.LogInformation("Restarting Docker Compose services with {Count} compose files in {WorkingDirectory}", 
                    composeFiles?.Length, workingDirectory);

                if (composeFiles == null || composeFiles.Length == 0)
                {
                    throw new ArgumentException("At least one compose file must be specified", nameof(composeFiles));
                }

                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    throw new ArgumentException("Working directory cannot be null or whitespace", nameof(workingDirectory));
                }

                // Build the docker-compose command with multiple -f flags
                var composeFileArgs = string.Join(" ", composeFiles.Select(f => $"-f \"{f}\""));
                var command = $"docker-compose {composeFileArgs} down && docker-compose {composeFileArgs} up -d";

                if (nohup)
                {
                    command = "nohup sh -c '" + command + "' > /dev/null 2>&1 &";
                }
                
                _logger.LogDebug("Executing Docker Compose command: {Command}", command);

                var result = await _sshService.ExecuteCommandAsync(command, workingDirectory);

                _logger.LogInformation("Docker Compose services restarted successfully");
                _logger.LogDebug("Docker Compose output: {Output}", result.Output);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restart Docker Compose services");
                throw;
            }
        }

    }
}