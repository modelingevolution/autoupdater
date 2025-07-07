using CliWrap;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;

/// <summary>
/// Manages Docker Compose operations for integration testing
/// </summary>
public class DockerComposeManager : IDisposable
{
    private readonly ILogger<DockerComposeManager> _logger;
    private readonly List<string> _activeServices = new();

    public DockerComposeManager(ILogger<DockerComposeManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts services defined in a Docker Compose file
    /// </summary>
    public async Task StartAsync(
        string composeFilePath,
        string? serviceName = null,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(composeFilePath))
            throw new FileNotFoundException($"Docker Compose file not found: {composeFilePath}");

        var serviceId = $"{composeFilePath}:{serviceName ?? "all"}";
        _logger.LogInformation("Starting Docker Compose services: {ServiceId}", serviceId);

        var args = new List<string> { "compose", "-f", composeFilePath, "up", "-d" };
        
        if (!string.IsNullOrEmpty(serviceName))
        {
            args.Add(serviceName);
        }

        var command = Cli.Wrap("docker")
            .WithArguments(args)
            .WithWorkingDirectory(Path.GetDirectoryName(composeFilePath) ?? Directory.GetCurrentDirectory());

        // Add environment variables if provided
        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            var envVars = environmentVariables.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value);
            command = command.WithEnvironmentVariables(envVars);
        }

        var result = await command
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .ExecuteAsync(cancellationToken);

        _activeServices.Add(serviceId);
        _logger.LogInformation("Successfully started Docker Compose services: {ServiceId}", serviceId);
    }

    /// <summary>
    /// Stops services defined in a Docker Compose file
    /// </summary>
    public async Task StopAsync(
        string composeFilePath,
        string? serviceName = null,
        bool removeVolumes = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(composeFilePath))
        {
            _logger.LogWarning("Docker Compose file not found during stop: {ComposeFilePath}", composeFilePath);
            return;
        }

        var serviceId = $"{composeFilePath}:{serviceName ?? "all"}";
        _logger.LogInformation("Stopping Docker Compose services: {ServiceId}", serviceId);

        var args = new List<string> { "compose", "-f", composeFilePath, "down" };
        
        if (!string.IsNullOrEmpty(serviceName))
        {
            args.AddRange(new[] { "--remove-orphans" });
        }

        if (removeVolumes)
        {
            args.Add("-v");
        }

        try
        {
            var result = await Cli.Wrap("docker")
                .WithArguments(args)
                .WithWorkingDirectory(Path.GetDirectoryName(composeFilePath) ?? Directory.GetCurrentDirectory())
                .WithValidation(CommandResultValidation.None) // Don't throw on non-zero exit code
                .ExecuteAsync(cancellationToken);

            if (result.ExitCode == 0)
            {
                _activeServices.Remove(serviceId);
                _logger.LogInformation("Successfully stopped Docker Compose services: {ServiceId}", serviceId);
            }
            else
            {
                _logger.LogWarning("Docker Compose stop returned exit code {ExitCode} for {ServiceId}", 
                    result.ExitCode, serviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping Docker Compose services: {ServiceId}", serviceId);
        }
    }

    /// <summary>
    /// Gets the status of services in a Docker Compose file
    /// </summary>
    public async Task<ComposeStatus> GetStatusAsync(
        string composeFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(composeFilePath))
            throw new FileNotFoundException($"Docker Compose file not found: {composeFilePath}");

        _logger.LogInformation("Getting status of Docker Compose services: {ComposeFilePath}", composeFilePath);

        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        var result = await Cli.Wrap("docker")
            .WithArguments(["compose", "-f", composeFilePath, "ps", "--format", "json"])
            .WithWorkingDirectory(Path.GetDirectoryName(composeFilePath) ?? Directory.GetCurrentDirectory())
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("Docker Compose ps failed with exit code {ExitCode}: {Error}", 
                result.ExitCode, stdErrBuffer.ToString());
            return new ComposeStatus { IsRunning = false, Services = new List<ServiceStatus>() };
        }

        var output = stdOutBuffer.ToString();
        var services = ParseComposeStatus(output);

        return new ComposeStatus
        {
            IsRunning = services.Any(s => s.State == "running"),
            Services = services
        };
    }

    /// <summary>
    /// Waits for a service to be healthy
    /// </summary>
    public async Task<bool> WaitForServiceHealthyAsync(
        string composeFilePath,
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Waiting for service {ServiceName} to become healthy (timeout: {Timeout})", 
            serviceName, timeout);

        var startTime = DateTime.UtcNow;
        var healthCheckInterval = TimeSpan.FromSeconds(2);

        while (DateTime.UtcNow - startTime < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var status = await GetStatusAsync(composeFilePath, cancellationToken);
                var service = status.Services.FirstOrDefault(s => s.Name == serviceName);

                if (service != null && service.State == "running" && service.Health == "healthy")
                {
                    _logger.LogInformation("Service {ServiceName} is healthy", serviceName);
                    return true;
                }

                if (service != null)
                {
                    _logger.LogDebug("Service {ServiceName} status: State={State}, Health={Health}", 
                        serviceName, service.State, service.Health);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking service health for {ServiceName}", serviceName);
            }

            await Task.Delay(healthCheckInterval, cancellationToken);
        }

        _logger.LogWarning("Service {ServiceName} did not become healthy within {Timeout}", serviceName, timeout);
        return false;
    }

    /// <summary>
    /// Gets logs from a specific service
    /// </summary>
    public async Task<string> GetServiceLogsAsync(
        string composeFilePath,
        string serviceName,
        int tailLines = 100,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting logs for service {ServiceName}", serviceName);

        var stdOutBuffer = new StringBuilder();

        var result = await Cli.Wrap("docker")
            .WithArguments(["compose", "-f", composeFilePath, "logs", "--tail", tailLines.ToString(), serviceName])
            .WithWorkingDirectory(Path.GetDirectoryName(composeFilePath) ?? Directory.GetCurrentDirectory())
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);

        return stdOutBuffer.ToString();
    }

    /// <summary>
    /// Executes a command in a running service container
    /// </summary>
    public async Task<string> ExecAsync(
        string composeFilePath,
        string serviceName,
        string command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Executing command in service {ServiceName}: {Command}", serviceName, command);

        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        var result = await Cli.Wrap("docker")
            .WithArguments(["compose", "-f", composeFilePath, "exec", "-T", serviceName, "sh", "-c", command])
            .WithWorkingDirectory(Path.GetDirectoryName(composeFilePath) ?? Directory.GetCurrentDirectory())
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed with exit code {result.ExitCode}: {stdErrBuffer}");
        }

        return stdOutBuffer.ToString();
    }

    /// <summary>
    /// Cleans up all active services
    /// </summary>
    public async Task CleanupAsync()
    {
        if (_activeServices.Count == 0)
        {
            _logger.LogInformation("No Docker Compose services to clean up");
            return;
        }

        _logger.LogInformation("Cleaning up {Count} Docker Compose services", _activeServices.Count);

        var cleanupTasks = _activeServices.ToList().Select(async serviceId =>
        {
            try
            {
                var parts = serviceId.Split(':');
                if (parts.Length >= 2)
                {
                    var composeFile = parts[0];
                    var serviceName = parts[1] == "all" ? null : parts[1];
                    await StopAsync(composeFile, serviceName, removeVolumes: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up service: {ServiceId}", serviceId);
            }
        });

        await Task.WhenAll(cleanupTasks);
        _activeServices.Clear();
    }

    /// <summary>
    /// Parses Docker Compose ps JSON output
    /// </summary>
    private List<ServiceStatus> ParseComposeStatus(string jsonOutput)
    {
        var services = new List<ServiceStatus>();

        if (string.IsNullOrWhiteSpace(jsonOutput))
            return services;

        try
        {
            // Simple JSON parsing for Docker Compose ps output
            // In production, you'd use a proper JSON library
            var lines = jsonOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                if (line.Trim().StartsWith("{") && line.Trim().EndsWith("}"))
                {
                    // Basic parsing - would use System.Text.Json in real implementation
                    var service = new ServiceStatus();
                    
                    if (line.Contains("\"Name\":"))
                    {
                        var nameStart = line.IndexOf("\"Name\":\"") + 8;
                        var nameEnd = line.IndexOf("\"", nameStart);
                        if (nameEnd > nameStart)
                        {
                            service.Name = line.Substring(nameStart, nameEnd - nameStart);
                        }
                    }
                    
                    if (line.Contains("\"State\":"))
                    {
                        var stateStart = line.IndexOf("\"State\":\"") + 9;
                        var stateEnd = line.IndexOf("\"", stateStart);
                        if (stateEnd > stateStart)
                        {
                            service.State = line.Substring(stateStart, stateEnd - stateStart);
                        }
                    }
                    
                    if (line.Contains("\"Health\":"))
                    {
                        var healthStart = line.IndexOf("\"Health\":\"") + 10;
                        var healthEnd = line.IndexOf("\"", healthStart);
                        if (healthEnd > healthStart)
                        {
                            service.Health = line.Substring(healthStart, healthEnd - healthStart);
                        }
                    }
                    
                    services.Add(service);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Docker Compose status output: {Output}", jsonOutput);
        }

        return services;
    }

    public void Dispose()
    {
        // Cleanup is async, so we can't do it in Dispose
        // Users should call CleanupAsync() explicitly
    }
}

/// <summary>
/// Status of Docker Compose services
/// </summary>
public class ComposeStatus
{
    public bool IsRunning { get; set; }
    public List<ServiceStatus> Services { get; set; } = new();
}

/// <summary>
/// Status of a single service
/// </summary>
public class ServiceStatus
{
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Health { get; set; } = string.Empty;
}