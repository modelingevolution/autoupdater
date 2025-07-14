using Docker.DotNet;
using Docker.DotNet.Models;
using Ductus.FluentDocker.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Extensions;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater;

public class UpdateHost : IHostedService
{
    private readonly IConfiguration _config;
    private readonly ILogger<UpdateHost> _log;
    private readonly IGitService _gitService;
    private readonly IScriptMigrationService _scriptMigrationService;
    private readonly ISshConnectionManager _sshConnectionManager;
    private readonly IDockerComposeService _dockerComposeService;
    private readonly IDeploymentStateProvider _deploymentStateProvider;
    private readonly GlobalSshConfiguration _sshConfig = new();
    
    public UpdateHost(
        IConfiguration config, 
        ILogger<UpdateHost> log,
        IGitService gitService,
        IScriptMigrationService scriptMigrationService,
        ISshConnectionManager sshConnectionManager,
        IDockerComposeService dockerComposeService,
        IDeploymentStateProvider deploymentStateProvider)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
        _scriptMigrationService = scriptMigrationService ?? throw new ArgumentNullException(nameof(scriptMigrationService));
        _sshConnectionManager = sshConnectionManager ?? throw new ArgumentNullException(nameof(sshConnectionManager));
        _dockerComposeService = dockerComposeService ?? throw new ArgumentNullException(nameof(dockerComposeService));
        _deploymentStateProvider = deploymentStateProvider ?? throw new ArgumentNullException(nameof(deploymentStateProvider));
    }
    
    public IDictionary<string, string> Volumes { get; private set; } = new Dictionary<string, string>();
    public ILogger Log => _log;
    
    public GlobalSshConfiguration SshConfig => _sshConfig;
    public static async Task<ContainerListResponse?> GetContainer(string imageName = "modelingevolution/autoupdater")
    {
        using var config = new DockerClientConfiguration();
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Initialize SSH configuration from appsettings
            // Use individual GetValue calls to properly handle environment variables and JSON config
            _sshConfig.SshUser = _config?.GetValue<string>("SshUser");
            _sshConfig.SshPwd = _config?.GetValue<string>("SshPwd");
            _sshConfig.SshKeyPath = _config?.GetValue<string>("SshKeyPath");
            _sshConfig.SshKeyPassphrase = _config?.GetValue<string>("SshKeyPassphrase");
            
            // Parse enum values safely
            if (Enum.TryParse<SshAuthMethod>(_config?.GetValue<string>("SshAuthMethod"), true, out var authMethod))
            {
                _sshConfig.SshAuthMethod = authMethod;
            }
            
            // Parse integer values with defaults
            _sshConfig.SshPort = _config?.GetValue<int?>("SshPort") ?? 22;
            _sshConfig.SshTimeoutSeconds = _config?.GetValue<int?>("SshTimeoutSeconds") ?? 30;
            _sshConfig.SshKeepAliveSeconds = _config?.GetValue<int?>("SshKeepAliveSeconds") ?? 30;
            
            // Parse boolean values with defaults
            _sshConfig.SshEnableCompression = _config?.GetValue<bool?>("SshEnableCompression") ?? true;
            
            // Initialize host address from configuration with fallback
            _hostAddress = _config?.GetValue<string>("SshHost")  ?? _config?.GetValue<string>("HostAddress") ?? "172.17.0.1";
            
            
            
            // Log configuration (without sensitive values)
            _log.LogInformation("=== AutoUpdater Configuration ===");
            _log.LogInformation("Host Address: {HostAddress}", _hostAddress);
            _log.LogInformation("SSH Configuration: {SshConfig}", _sshConfig.GetSafeConfigurationSummary());


            var cid = (await GetContainer())?.ID;
            if (cid != null)
            {
                this.Volumes = await _dockerComposeService.GetVolumeMappingsAsync(cid);
                _log.LogInformation("Docker volume mapping configured [{Count}]. Mappings: {Mappings}",
                    this.Volumes.Count,
                    string.Join(", ", this.Volumes.Select(kvp => $"{kvp.Key}->{kvp.Value}")));
            }
            else
                _log.LogInformation("Docker volume mapping is disabled.");

            // Test SSH connectivity
            var connectivityTest = await _sshConnectionManager.TestConnectivityAsync();
            if (!connectivityTest)
            {
                throw new InvalidOperationException("SSH connectivity test failed");
            }
            _log.LogInformation("AutoUpdater SSH connectivity test successful");


            _log.LogInformation("=== AutoUpdater Startup Complete ===");
        }
        catch (Exception ex) {
            _log.LogError(ex, "Cannot start {ServiceName}", nameof(UpdateHost));
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    private string _hostAddress = "172.17.0.1";

    /// <summary>
    /// Orchestrates the update process using the injected services
    /// </summary>
    /// <param name="configuration">The Docker Compose configuration to update</param>
    /// <returns>Result of the update operation</returns>
    public async Task<UpdateResult> UpdateAsync(DockerComposeConfiguration configuration)
    {
        var executedScripts = new List<string>();
        
        try
        {
            _log.LogInformation("Starting update process for {RepositoryLocation}", configuration.RepositoryLocation);

            // Step 1: Get current and available versions
            var currentVersion = await _deploymentStateProvider.GetCurrentVersionAsync(configuration.ComposeFolderPath);
            var availableVersions = await _gitService.GetAvailableVersionsAsync(configuration.RepositoryLocation);
            var latestVersion = availableVersions.OrderByDescending(v => v.Version).FirstOrDefault();

            if (latestVersion == null)
            {
                _log.LogWarning("No versions found in repository {RepositoryUrl}", configuration.RepositoryUrl);
                return new UpdateResult(true, currentVersion, DateTime.Now, executedScripts);
            }

            // Step 2: Check if update is needed
            if (currentVersion != null && currentVersion == latestVersion.FriendlyName)
            {
                _log.LogInformation("Already at latest version {Version}", currentVersion);
                return new UpdateResult(true, currentVersion, DateTime.Now, executedScripts);
            }

            _log.LogInformation("Updating from {CurrentVersion} to {TargetVersion}", 
                currentVersion ?? "initial", latestVersion.FriendlyName);

            // Step 3: Checkout the target version
            await _gitService.CheckoutVersionAsync(configuration.RepositoryLocation, latestVersion.FriendlyName);

            // Step 4: Detect architecture and get appropriate compose files
            using var sshService = await _sshConnectionManager.CreateSshServiceAsync();
            var architecture = await sshService.GetArchitectureAsync();
            var composeFiles = await _dockerComposeService.GetComposeFilesForArchitectureAsync(
                configuration.ComposeFolderPath, architecture);

            // Step 5: Execute migration scripts
            var allScripts = await _scriptMigrationService.DiscoverScriptsAsync(configuration.ComposeFolderPath);
            var scriptsToExecute = await _scriptMigrationService.FilterScriptsForMigrationAsync(
                allScripts, currentVersion, latestVersion.FriendlyName);

            if (scriptsToExecute.Any())
            {
                _log.LogInformation("Executing {Count} migration scripts", scriptsToExecute.Count());
                await _scriptMigrationService.ExecuteScriptsAsync(scriptsToExecute, configuration.ComposeFolderPath);
                executedScripts.AddRange(scriptsToExecute.Select(s => s.FileName));
            }

            // Step 6: Start Docker Compose services
            await _dockerComposeService.StartServicesAsync(composeFiles, configuration.ComposeFolderPath);

            // Step 7: Update deployment state
            var deploymentState = new DeploymentState(latestVersion.FriendlyName, DateTime.Now);
            await _deploymentStateProvider.SaveDeploymentStateAsync(configuration.ComposeFolderPath, deploymentState);

            _log.LogInformation("Update completed successfully to version {Version}", latestVersion.FriendlyName);
            return new UpdateResult(true, latestVersion.FriendlyName, DateTime.Now, executedScripts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update failed: {Message}", ex.Message);
            return new UpdateResult(false, null, DateTime.Now, executedScripts, ex.Message);
        }
    }
}