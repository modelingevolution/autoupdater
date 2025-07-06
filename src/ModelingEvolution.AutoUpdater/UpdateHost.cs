using Docker.DotNet;
using Docker.DotNet.Models;
using Ductus.FluentDocker.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace ModelingEvolution.AutoUpdater;

public class UpdateHost(IConfiguration config, ILogger<UpdateHost> log) : IHostedService
{
    private readonly GlobalSshConfiguration _sshConfig = new();
    
    public IDictionary<string, string> Volumes { get; private set; } = new Dictionary<string, string>();
    public ILogger Log => log;
    
    public GlobalSshConfiguration SshConfig => _sshConfig;
    public static async Task<ContainerListResponse?> GetContainer(string imageName = "modelingevolution/autoupdater")
    {
        using var config = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"));
        using var client = config.CreateClient();
        var filters = new Dictionary<string, IDictionary<string, bool>> { { "status", new Dictionary<string, bool> { { "running", true } } } };

        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { Filters = filters });

        ContainerListResponse? c = containers.FirstOrDefault(c => c.Image.Contains(imageName));
        return c;
    }
    private static async Task<ContainerLogs> GetContainerLogs(string containerId)
    {
        using var config = new DockerClientConfiguration();
        using var client = config.CreateClient();

        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = false,
            Tail = "all"
        };

        using (var stream = await client.Containers.GetContainerLogsAsync(containerId,true, parameters, CancellationToken.None))
        {
            var (o,e) = await stream.ReadOutputToEndAsync(CancellationToken.None);
            return new ContainerLogs(o, e);
        }
    }
    public static async Task<IDictionary<string, string>> GetVolumeMappings(string containerId)
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Initialize SSH configuration from appsettings
            config.GetSection("").Bind(_sshConfig);
            
            // Log configuration (without sensitive values)
            log.LogInformation("=== AutoUpdater Configuration ===");
            log.LogInformation("SSH Configuration: {SshConfig}", _sshConfig.GetSafeConfigurationSummary());
            
            var cid = (await GetContainer())?.ID;
            if (cid != null)
            {
                this.Volumes = await GetVolumeMappings(cid);
                log.LogInformation("Docker volume mapping configured [{Count}]. Mappings: {Mappings}", 
                    this.Volumes.Count, 
                    string.Join(", ", this.Volumes.Select(kvp => $"{kvp.Key}->{kvp.Value}")));
            }
            else
                log.LogInformation("Docker volume mapping is disabled.");
            
            // Test SSH connectivity
            await InvokeSsh("echo \"AutoUpdater SSH connectivity test successful\"");
            log.LogInformation("=== AutoUpdater Startup Complete ===");
        }
        catch (Exception ex) {
            log.LogError(ex, "Cannot start {ServiceName}", nameof(UpdateHost));
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    //const string HostAddress = "host.docker.internal";
    const string HostAddress = "172.17.0.1";

    //const string HostAddress = "pi-200";
    internal async Task<String> InvokeSsh(string command, string? dockerComposeFolder = null, Action? onExecuted = null)
    {
        if (string.IsNullOrEmpty(_sshConfig.SshUser))
            throw new InvalidOperationException("SSH user is not configured. Set SshUser in configuration.");

        // Create SSH configuration for the host
        var sshConfig = _sshConfig.ToSshConfiguration(HostAddress);
        
        // Create SSH connection manager
        using var sshManager = new SshConnectionManager(sshConfig, log);
        
        try
        {
            // Create and connect SSH client
            using var client = await sshManager.CreateConnectionAsync();
            
            // Execute command
            var result = await sshManager.ExecuteCommandAsync(command, dockerComposeFolder);
            
            // Call completion callback
            onExecuted?.Invoke();
            
            if (result.IsSuccess)
            {
                log.LogInformation("SSH command executed successfully: {Command}", command);
                log.LogDebug("SSH command output: {Output}", result.Output);
            }
            else
            {
                log.LogWarning("SSH command failed with exit code {ExitCode}: {Command}. Error: {Error}", 
                    result.ExitCode, command, result.Error);
            }
            
            return result.Output;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SSH command execution failed: {Command}", command);
            throw;
        }
    }
}