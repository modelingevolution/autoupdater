using Docker.DotNet;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;
using ModelingEvolution.AutoUpdater.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
        private readonly IEventHub? _eventHub;
        
        // Caching for GetDockerComposeStatusAsync
        private Dictionary<PackageName, ComposeProjectStatus>? _cachedStatus;
        private DateTime _lastCacheTime = DateTime.MinValue;
        private static readonly TimeSpan CacheTimeout = TimeSpan.FromSeconds(5);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
        
        // Docker Compose command detection
        private string? _dockerComposeCommand;
        private readonly SemaphoreSlim _commandDetectionLock = new(1, 1);

        public DockerComposeService(ISshService sshService, ILogger<DockerComposeService> logger, IEventHub? eventHub = null)
        {
            _sshService = sshService ?? throw new ArgumentNullException(nameof(sshService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventHub = eventHub;
        }

        /// <summary>
        /// Detects and returns the appropriate Docker Compose command
        /// </summary>
        private async Task<string> GetDockerComposeCommandAsync()
        {
            if (_dockerComposeCommand != null)
            {
                return _dockerComposeCommand;
            }

            await _commandDetectionLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (_dockerComposeCommand != null)
                {
                    return _dockerComposeCommand;
                }

                _logger.LogDebug("Detecting Docker Compose command...");

                // Try docker compose (v2) first
                var result = await _sshService.ExecuteCommandAsync("sudo docker compose version");
                if (result.IsSuccess && result.Output.Contains("Docker Compose"))
                {
                    _dockerComposeCommand = "docker compose";
                    _logger.LogInformation("Detected Docker Compose v2 (docker compose)");
                    return _dockerComposeCommand;
                }

                // Try docker-compose (v1)
                result = await _sshService.ExecuteCommandAsync("sudo docker-compose --version");
                if (result.IsSuccess && result.Output.Contains("docker-compose"))
                {
                    _dockerComposeCommand = "docker-compose";
                    _logger.LogInformation("Detected Docker Compose v1 (docker-compose)");
                    return _dockerComposeCommand;
                }

                // Default to v2 syntax if detection fails
                _logger.LogWarning("Could not detect Docker Compose version, defaulting to 'docker compose' (v2)");
                _dockerComposeCommand = "docker compose";
                return _dockerComposeCommand;
            }
            finally
            {
                _commandDetectionLock.Release();
            }
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
                    .Select(x=> $".{x}.")
                    .ToHashSet();

                var composeFiles = _sshService
                    .GetFiles(directoryPath, "docker-compose*yml")
                    .Where(x => !notValid.Any(x.Contains))
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
                var composeCommand = await GetDockerComposeCommandAsync();
                var command = $"sudo {composeCommand} {composeFileArgs} up -d";

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
                var composeCommand = await GetDockerComposeCommandAsync();
                var command = $"sudo {composeCommand} {composeFileArgs} down";

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

                var composeCommand = await GetDockerComposeCommandAsync();
                var command = $"sudo {composeCommand} -p \"{projectName}\" down";

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

                var composeCommand = await GetDockerComposeCommandAsync();
                var command = $"sudo {composeCommand} -p \"{projectName}\" ps --format json";

                _logger.LogDebug("Executing Docker Compose command: {Command}", command);

                var result = await _sshService.ExecuteCommandAsync(command);

                if (!result.IsSuccess)
                {
                    _logger.LogWarning("Failed to get project status for {ProjectName}: {Error}", projectName, result.Error);
                    return new ComposeProjectStatus("unknown", [], 0, 0);
                }

                // Parse the JSON output to determine project status
                var output = result.Output.Trim();
                var isRunning = !string.IsNullOrEmpty(output) && output != "[]";
                var status = isRunning ? "running" : "stopped";
                var runningServices = isRunning ? 1 : 0; // Simplified - would need JSON parsing for accurate count
                var totalServices = isRunning ? 1 : 0;   // Simplified - would need JSON parsing for accurate count

                _logger.LogDebug("Project {ProjectName} status: {Status}", projectName, status);
                return new ComposeProjectStatus(status, [], runningServices, totalServices);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get status for Docker Compose project: {ProjectName}", projectName);
                return new ComposeProjectStatus("error", [], 0, 0);
            }
        }

        public async Task PullImagesAsync(string[] composeFiles, string workingDirectory)
        {
            await PullAsync(composeFiles, workingDirectory, TimeSpan.FromMinutes(10));
        }

        public async Task PullAsync(string[] composeFiles, string workingDirectory, TimeSpan timeout)
        {
            try
            {
                _logger.LogInformation("Pulling Docker images for {Count} compose files in {WorkingDirectory} with timeout {Timeout}", 
                    composeFiles?.Length, workingDirectory, timeout);

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
                var composeCommand = await GetDockerComposeCommandAsync();
                var command = $"sudo {composeCommand} {composeFileArgs} pull";

                _logger.LogDebug("Executing Docker Compose command with timeout {Timeout}: {Command}", timeout, command);

                var result = await _sshService.ExecuteCommandAsync(command, timeout, workingDirectory);

                if (!result.IsSuccess)
                {
                    _logger.LogError("Failed to pull Docker images: {Error}", result.Error);
                    throw new InvalidOperationException($"Failed to pull Docker images: {result.Error}");
                }

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
                var composeCommand = await GetDockerComposeCommandAsync();
                var command = $"sudo {composeCommand} {composeFileArgs} ps";

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

        public async Task RestartServicesAsync(string[] composeFiles, string workingDirectory, bool nohup = false,
            string? cmd = null)
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
                var composeCommand = await GetDockerComposeCommandAsync();
                var command = $"sudo {composeCommand} {composeFileArgs} down && sudo {composeCommand} {composeFileArgs} up -d";
                if(!string.IsNullOrWhiteSpace(cmd))
                    command += $" && {cmd}";
                
                if (nohup)
                {
                    command = $"nohup sh -c '{command}' > /dev/null 2>&1 &";
                }
                
                _logger.LogInformation("Executing Docker Compose command: {Command}", command);

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

        public async Task<Dictionary<PackageName, ComposeProjectStatus>> GetDockerComposeStatusAsync()
        {
            try
            {
                // Check if we have cached data that's still valid
                if (_cachedStatus != null && DateTime.Now - _lastCacheTime < CacheTimeout)
                {
                    _logger.LogDebug("Returning cached Docker Compose status (age: {Age}ms)", 
                        (DateTime.Now - _lastCacheTime).TotalMilliseconds);
                    return _cachedStatus;
                }

                _logger.LogDebug("Fetching fresh Docker Compose status");
                
                var composeCommand = await GetDockerComposeCommandAsync();
                var command = $"sudo {composeCommand} ls --format json";
                var result = await _sshService.ExecuteCommandAsync(command);
                
                if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Output))
                {
                    _logger.LogWarning("No output from docker-compose ls command");
                    return new Dictionary<PackageName, ComposeProjectStatus>();
                }

                // Define ComposeProject record locally since it might not be available here
                var composeProjects = JsonSerializer.Deserialize<ComposeProjectInfo[]>(result.Output, JsonOptions);

                if (composeProjects == null)
                {
                    _logger.LogWarning("Failed to deserialize docker-compose ls output");
                    return new Dictionary<PackageName, ComposeProjectStatus>();
                }

                var statusMap = composeProjects.ToDictionary(
                    project => new PackageName(project.Name),
                    project =>
                    {
                        // Parse service count from status string (e.g., "running(2)" -> 2)
                        var totalServices = ParseServiceCount(project.Status);
                        var isRunning = project.Status.Contains("running", StringComparison.OrdinalIgnoreCase);

                        return new ComposeProjectStatus(
                            project.Status,
                            project.ConfigFiles?.Split(',').ToImmutableArray() ?? [],
                            isRunning ? totalServices : 0,
                            totalServices
                        );
                    });

                // Detect and publish status changes
                if (_eventHub != null && _cachedStatus != null)
                {
                    foreach (var kvp in statusMap)
                    {
                        var packageName = kvp.Key;
                        var newStatus = kvp.Value.Status;
                        
                        // Check if this package existed in the previous cache
                        if (_cachedStatus.TryGetValue(packageName, out var oldProjectStatus))
                        {
                            var oldStatus = oldProjectStatus.Status;
                            
                            // If status changed, publish event
                            if (!string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogInformation("Package {PackageName} status changed from {OldStatus} to {NewStatus}",
                                    packageName, oldStatus, newStatus);
                                    
                                await _eventHub.PublishAsync(new PackageStatusChangedEvent(packageName, newStatus, oldStatus));
                            }
                        }
                        else
                        {
                            // New package detected
                            _logger.LogInformation("New package {PackageName} detected with status {Status}",
                                packageName, newStatus);
                                
                            await _eventHub.PublishAsync(new PackageStatusChangedEvent(packageName, newStatus));
                        }
                    }
                    
                    // Check for removed packages
                    foreach (var oldKvp in _cachedStatus)
                    {
                        if (!statusMap.ContainsKey(oldKvp.Key))
                        {
                            _logger.LogInformation("Package {PackageName} removed", oldKvp.Key);
                            await _eventHub.PublishAsync(new PackageStatusChangedEvent(oldKvp.Key, "removed", oldKvp.Value.Status));
                        }
                    }
                }

                // Update cache
                _cachedStatus = statusMap;
                _lastCacheTime = DateTime.Now;

                _logger.LogDebug("Retrieved and cached status for {Count} compose projects", statusMap.Count);
                return statusMap;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Docker Compose status via SSH");
                return new Dictionary<PackageName, ComposeProjectStatus>();
            }
        }

        /// <summary>
        /// Parses the service count from docker compose ls status string.
        /// Status format: "running(2)", "exited(1)", etc.
        /// </summary>
        private static int ParseServiceCount(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return 0;

            var startIndex = status.IndexOf('(');
            var endIndex = status.IndexOf(')');

            if (startIndex >= 0 && endIndex > startIndex)
            {
                var countStr = status.Substring(startIndex + 1, endIndex - startIndex - 1);
                if (int.TryParse(countStr, out var count))
                {
                    return count;
                }
            }

            // Fallback to 1 if we can't parse the count
            return 1;
        }

        // Local record for JSON deserialization
        private record ComposeProjectInfo
        {
            public string Name { get; init; } = string.Empty;
            public string Status { get; init; } = string.Empty;
            public string? ConfigFiles { get; init; }
        }
    }
}