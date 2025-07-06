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
    public IDictionary<string, string> Volumes { get; private set; }
    public ILogger Log => log;
    public static async Task<ContainerListResponse> GetContainer(string imageName = "modelingevolution/autoupdater")
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
            var cid = (await GetContainer())?.ID;
            if (cid != null)
            {
                this.Volumes = await GetVolumeMappings(cid);
                log.LogInformation($"Docker volume mapping configured [{this.Volumes.Count}].");
            }
            else
                log.LogInformation("Docker volume mapping is disabled.");
            await InvokeSsh("echo \"Hello\";");
        }
        catch (Exception ex) {
            log.LogError(ex, $"Cannot start {nameof(UpdateHost)}.");
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
        var usr = config.GetValue<string>("SshUser") ?? throw new ArgumentException("Ssh user cannot be null");
        var pwd = config.GetValue<string>("SshPwd") ?? throw new ArgumentException("Ssh password cannot be null");
        using (var client = new SshClient(HostAddress, usr, pwd))
        {
                
            client.ServerIdentificationReceived += (s, e) => e.ToSuccess();
            client.HostKeyReceived += (sender, e) => {
                e.CanTrust = true;
            };
            client.Connect();

            if (dockerComposeFolder != null)
                command = $"cd {dockerComposeFolder}; " + command;
            using SshCommand cmd = client.RunCommand(command);

            onExecuted?.Invoke();
            log.LogInformation($"Ssh: {command}, results: {cmd.Result}");
            return cmd.Result;

        }
    }
}