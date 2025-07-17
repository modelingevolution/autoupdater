using Docker.DotNet;
using Docker.DotNet.Models;
using Ductus.FluentDocker.Common;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;
using ModelingEvolution.AutoUpdater.Extensions;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemVersion = System.Version;

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
    private readonly IBackupService _backupService;
    private readonly IHealthCheckService _healthCheckService;
    private readonly IProgressService _progressService;
    private readonly IEventHub _eventHub;
    private readonly GlobalSshConfiguration _sshConfig = new();
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    
    public UpdateHost(
        IConfiguration config, 
        ILogger<UpdateHost> log,
        IGitService gitService,
        IScriptMigrationService scriptMigrationService,
        ISshConnectionManager sshConnectionManager,
        IDockerComposeService dockerComposeService,
        IDeploymentStateProvider deploymentStateProvider,
        IBackupService backupService,
        IHealthCheckService healthCheckService,
        IProgressService progressService,
        IEventHub eventHub)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
        _scriptMigrationService = scriptMigrationService ?? throw new ArgumentNullException(nameof(scriptMigrationService));
        _sshConnectionManager = sshConnectionManager ?? throw new ArgumentNullException(nameof(sshConnectionManager));
        _dockerComposeService = dockerComposeService ?? throw new ArgumentNullException(nameof(dockerComposeService));
        _deploymentStateProvider = deploymentStateProvider ?? throw new ArgumentNullException(nameof(deploymentStateProvider));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
        _progressService = progressService ?? throw new ArgumentNullException(nameof(progressService));
        _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));
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
            // Initialize SSH configuration from appsettings using extension methods
            _sshConfig.SshUser = _config?.SshUser();
            _sshConfig.SshPwd = _config?.SshPassword();
            _sshConfig.SshKeyPath = _config?.SshKeyPath();
            _sshConfig.SshKeyPassphrase = _config?.SshKeyPassphrase();
            
            // Parse enum values safely
            if (Enum.TryParse<SshAuthMethod>(_config?.GetValue<string>("SshAuthMethod"), true, out var authMethod))
            {
                _sshConfig.SshAuthMethod = authMethod;
            }
            
            // Parse integer values with defaults using extension methods
            _sshConfig.SshPort = _config?.SshPort() ?? 22;
            _sshConfig.SshTimeoutSeconds = _config?.SshTimeoutSeconds() ?? 30;
            _sshConfig.SshKeepAliveSeconds = _config?.SshKeepAliveSeconds() ?? 30;
            
            // Parse boolean values with defaults using extension methods
            _sshConfig.SshEnableCompression = _config?.SshEnableCompression() ?? true;
            
            // Initialize host address from configuration with fallback
            _hostAddress = _config?.SshHost() ?? _config?.GetValue<string>("HostAddress") ?? "172.17.0.1";
            
            
            
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
    /// Orchestrates the update process following the complete decision tree workflow
    /// </summary>
    /// <param name="configuration">The Docker Compose configuration to update</param>
    /// <returns>Result of the update operation</returns>
    public async Task<UpdateResult> UpdateAsync(DockerComposeConfiguration configuration)
    {
        // Ensure only one update can run at a time
        if (!await _updateLock.WaitAsync(100))
        {
            _log.LogWarning("Update already in progress, request rejected");
            return UpdateResult.CreateFailed("Update already in progress", null, new List<string>());
        }

        try
        {
            _progressService.StartOperation("Initializing update", configuration.FriendlyName, 1);
            
            BackupResult? backup = null;
            var executedScripts = new List<string>();
            var executedVersions = new List<SystemVersion>();
            string? currentVersion = null;
            GitTagVersion? latestVersion = null;
            
            try
            {
            _progressService.LogOperationProgress("Loading deployment state", 10, "Starting update process for {RepositoryLocation}", configuration.RepositoryLocation);

            // Step 1: Load current deployment state
            var currentDeploymentState = await _deploymentStateProvider.GetDeploymentStateAsync(configuration.ComposeFolderPath);
            currentVersion = currentDeploymentState?.Version;

            await ConfigureGitRepositoryIfNeeded(configuration);

            await _gitService.FetchAsync(configuration.RepositoryLocation);
            
            latestVersion = await GetLatestVersion(configuration);

            if (latestVersion == null)
            {
                _log.LogWarning("No versions found in repository {RepositoryUrl}", configuration.RepositoryUrl);
                return UpdateResult.CreateSuccess(currentVersion ?? "unknown", currentVersion, executedScripts, HealthCheckResult.Healthy(new List<string>()));
            }

            // Step 2: Check if update is needed
            if (currentVersion != null && currentVersion == latestVersion.FriendlyName)
            {
                _log.LogInformation("Already at latest version {Version}", currentVersion);
                return UpdateResult.CreateSuccess(currentVersion, currentVersion, executedScripts,
                    HealthCheckResult.Healthy(new List<string>()));
            }

            _log.LogInformation("Updating from {CurrentVersion} to {TargetVersion}", currentVersion ?? "initial", latestVersion.FriendlyName);

            // Publish update started event
            await _eventHub.PublishAsync(new UpdateStartedEvent(configuration.FriendlyName, currentVersion, latestVersion.FriendlyName));

            // Step 3: Checkout the target version
            _progressService.LogOperationProgress("Checking out target version", 20);
            await _gitService.CheckoutVersionAsync(configuration.RepositoryLocation, latestVersion.FriendlyName);

            // Phase 1: Backup Creation (Decision Point: Backup Script Exists?)
            _progressService.LogOperationProgress("Creating backup", 30);
            if (await _backupService.BackupScriptExistsAsync(configuration.ComposeFolderPath))
            {
                _log.LogInformation("Creating backup before update");
                backup = await _backupService.CreateBackupAsync(configuration.ComposeFolderPath);
                
                if (!backup.Success)
                {
                    _log.LogError("Backup creation failed: {Error}", backup.Error);
                    return UpdateResult.CreateFailed(
                        $"Backup creation failed - cannot proceed: {backup.Error}",
                        currentVersion, executedScripts);
                }
                
                _log.LogInformation("Backup created successfully: {BackupFile}", backup.BackupFilePath);
            }
            else
            {
                _log.LogWarning("No backup script found - proceeding without backup");
            }

            // Step 4: Get architecture and compose files
            using var sshService = await _sshConnectionManager.CreateSshServiceAsync();
            var architecture = await sshService.GetArchitectureAsync();
            var composeFiles = await _dockerComposeService.GetComposeFiles(configuration.ComposeFolderPath, architecture);

            // Phase 2: Stop Current Services
            _progressService.LogOperationProgress("Stopping services", 40, "Stopping current Docker Compose services");
            var isSelfUpdating = configuration.FriendlyName == "autoupdater";
            if (!isSelfUpdating) 
                await _dockerComposeService.StopServicesAsync(composeFiles, configuration.ComposeFolderPath);
            else _log.LogInformation("Self-updating service detected - skipping stop phase for {ServiceName}", configuration.FriendlyName);
            
                // Phase 3: Migration Scripts (Decision Point: All Scripts Successful?)
                _progressService.LogOperationProgress("Executing migration scripts", 50);
            try
            {
                var allScripts = await _scriptMigrationService.DiscoverScriptsAsync(configuration.ComposeFolderPath);
                var excludeVersions = currentDeploymentState?.Up ?? ImmutableSortedSet<SystemVersion>.Empty;
                var scriptsToExecute = await _scriptMigrationService.FilterScriptsForMigrationAsync(
                    allScripts, currentVersion, latestVersion.FriendlyName, excludeVersions);

                if (scriptsToExecute.Any())
                {
                    _log.LogInformation("Executing {Count} migration scripts", scriptsToExecute.Count());
                    executedVersions.AddRange(await _scriptMigrationService.ExecuteScriptsAsync(scriptsToExecute, configuration.ComposeFolderPath));
                    executedScripts.AddRange(scriptsToExecute.Select(s => s.FileName));
                    
                    _log.LogInformation("Migration scripts executed successfully");
                }
            }
            catch (Exception migrationEx)
            {
                _log.LogError(migrationEx, "Migration script execution failed");
                
                // Decision Point: Backup Available?
                if (backup?.Success == true)
                {
                    _log.LogInformation("Performing rollback with backup recovery");
                    await PerformRollbackWithBackupAsync(executedVersions, backup, composeFiles, configuration.ComposeFolderPath);
                    
                    return UpdateResult.CreateFailed($"Migration failed: {migrationEx.Message}", currentVersion, executedScripts, recoveryPerformed: true, backup.BackupFilePath);
                }
                else
                {
                    return UpdateResult.CreateFailed($"Migration failed: {migrationEx.Message} - No recovery possible without backup", currentVersion, executedScripts, recoveryPerformed: false);
                }
            }

            // Phase 4: Start New Services (Decision Point: docker-compose up)
            _progressService.LogOperationProgress("Starting services", 70);
            try
            {
                _log.LogInformation("Starting new Docker Compose services");
                if(!isSelfUpdating)
                    await _dockerComposeService.StartServicesAsync(composeFiles, configuration.ComposeFolderPath);
                else await _dockerComposeService.RestartServicesAsync(composeFiles, configuration.ComposeFolderPath,true);
            }
            catch (Exception dockerEx)
            {
                _log.LogError(dockerEx, "Docker Compose startup failed");
                
                // Decision Point: Backup Available?
                if (backup?.Success == true)
                {
                    _log.LogInformation("Docker startup failed - performing rollback with backup recovery");
                    await PerformRollbackWithBackupAsync(executedVersions, backup, composeFiles, configuration.ComposeFolderPath);
                    
                    return UpdateResult.CreateFailed($"Docker startup failed: {dockerEx.Message}", currentVersion, executedScripts, recoveryPerformed: true, backup.BackupFilePath);
                }
                else
                {
                    return UpdateResult.CreateFailed($"Docker startup failed: {dockerEx.Message} - No recovery possible without backup", currentVersion, executedScripts, recoveryPerformed: false);
                }
            }

            // Phase 5: Health Check (Decision Point: All Services Healthy?)
            _progressService.LogOperationProgress("Performing health checks", 80, "Performing health check on all services");
            var healthCheck = await _healthCheckService.CheckServicesHealthAsync(composeFiles, configuration.ComposeFolderPath);
            
            if (!healthCheck.AllHealthy)
            {
                _log.LogWarning("Health check failed - some services are unhealthy: {UnhealthyServices}", 
                    string.Join(", ", healthCheck.UnhealthyServices));
                
                // Decision Point: Backup Available?
                if (backup?.Success == true && healthCheck.CriticalFailure)
                {
                    _log.LogInformation("Critical services failed - performing rollback with backup recovery");
                    await PerformRollbackWithBackupAsync(executedVersions, backup, composeFiles, configuration.ComposeFolderPath);
                    
                    return UpdateResult.CreateFailed(
                        "Critical services unhealthy after deployment",
                        currentVersion, executedScripts, recoveryPerformed: true, backup.BackupFilePath);
                }
                
                // Partial success - keep running services
                _log.LogInformation("Accepting partial deployment state - some services healthy");
                await UpdateDeploymentStateAsync(currentDeploymentState, latestVersion.FriendlyName, executedVersions, configuration.ComposeFolderPath);
                
                return UpdateResult.CreatePartialSuccess(
                    latestVersion.FriendlyName, currentVersion, executedScripts, healthCheck);
            }

            // Phase 6: Complete Success
            _progressService.LogOperationProgress("Finalizing update", 90, "All services healthy - update completed successfully");
            await UpdateDeploymentStateAsync(currentDeploymentState, latestVersion.FriendlyName, executedVersions, configuration.ComposeFolderPath);
            
            // Publish successful update completion event
            await _eventHub.PublishAsync(new UpdateCompletedEvent(
                configuration.FriendlyName,
                currentVersion,
                latestVersion.FriendlyName,
                true,
                null,
                executedScripts));
            
            // Cleanup backup on complete success
            if (backup?.Success == true)
            {
                _log.LogInformation("Cleaning up backup file: {BackupFile}", backup.BackupFilePath);
                // Note: We don't have a cleanup method in IBackupService yet, but we should add one
            }

            return UpdateResult.CreateSuccess(
                latestVersion.FriendlyName, currentVersion, executedScripts, healthCheck, backup?.BackupFilePath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error during update process");
            
            // Publish failed update completion event
            await _eventHub.PublishAsync(new UpdateCompletedEvent(
                configuration.FriendlyName,
                currentVersion,
                latestVersion?.FriendlyName ?? "unknown",
                false,
                ex.Message,
                executedScripts));
            
            // Unexpected failure - try to recover if possible
            if (backup?.Success == true)
            {
                try
                {
                    _log.LogInformation("Attempting emergency rollback due to unexpected error");
                    using var sshService = await _sshConnectionManager.CreateSshServiceAsync();
                    var architecture = await sshService.GetArchitectureAsync();
                    var composeFiles = await _dockerComposeService.GetComposeFiles(
                        configuration.ComposeFolderPath, architecture);
                    
                    await PerformRollbackWithBackupAsync(executedVersions, backup, composeFiles, configuration.ComposeFolderPath);
                    
                    return UpdateResult.CreateFailed(
                        $"Unexpected error: {ex.Message}",
                        currentVersion, executedScripts, recoveryPerformed: true, backup.BackupFilePath);
                }
                catch (Exception rollbackEx)
                {
                    _log.LogError(rollbackEx, "Emergency rollback also failed");
                    return UpdateResult.CreateRecoverableFailure(
                        $"Unexpected error: {ex.Message}. Rollback failed: {rollbackEx.Message}",
                        currentVersion, executedScripts, backup.BackupFilePath);
                }
            }
            
            return UpdateResult.CreateFailed(
                $"Unexpected error: {ex.Message}",
                currentVersion, executedScripts, recoveryPerformed: false);
            }
            finally
            {
                _progressService.CompleteOperation();
            }
        }
        finally
        {
            _updateLock.Release();
        }
    }
    /// <summary>
    /// Checks if an update is available for the specified Docker Compose configuration.
    /// </summary>
    /// <param name="configuration">
    /// The <see cref="DockerComposeConfiguration"/> containing the repository location and other configuration details.
    /// </param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation, with a result of <c>true</c> if an update is available; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method performs the following steps:
    /// 1. Retrieves the current deployment state.
    /// 2. Fetches the latest changes from the repository.
    /// 3. Determines the latest version available.
    /// 4. Compares the current version with the latest version.
    /// 5. Publishes a <see cref="VersionCheckCompletedEvent"/> to the event hub with the results of the version check.
    /// 6. Logs the outcome of the version check.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown if the <paramref name="configuration"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the deployment state or latest version cannot be determined.
    /// </exception>
    public async Task<bool> CheckIsUpdateAvailable(DockerComposeConfiguration configuration)
    {
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        
        var st = await _deploymentStateProvider.GetDeploymentStateAsync(configuration.ComposeFolderPath);
        await ConfigureGitRepositoryIfNeeded(configuration);

        await _gitService.FetchAsync(configuration.RepositoryLocation);
        var latest = await GetLatestVersion(configuration);
        var result = st?.Version == (latest?.FriendlyName ?? "-");

        var versionCheckEvent = new VersionCheckCompletedEvent(
            configuration.FriendlyName,
            st?.Version ?? "-",
            latest?.FriendlyName ?? "-",
            result
        );

        await this._eventHub.PublishAsync(versionCheckEvent);
        _log.LogInformation("Version check completed for package: {PackageName}, Current: {CurrentVersion}, Latest: {LatestVersion}, UpgradeAvailable: {UpgradeAvailable}",
            configuration.FriendlyName, st?.Version, latest?.FriendlyName, result);
        
        return result;
    }
    public async Task<GitTagVersion?> GetLatestVersion(DockerComposeConfiguration configuration)
    {
        GitTagVersion? latestVersion;
        var availableVersions = await _gitService.GetAvailableVersionsAsync(configuration.RepositoryLocation);
        latestVersion = availableVersions.OrderByDescending(v => v.Version).FirstOrDefault();
        return latestVersion;
    }

    private async Task ConfigureGitRepositoryIfNeeded(DockerComposeConfiguration configuration)
    {
        if (!_gitService.IsGitRepository(configuration.RepositoryLocation))
        {
            if (!Directory.Exists(configuration.RepositoryLocation))
            {
                _progressService.LogOperationProgress("Cloning repository", null, "Repository not found at {RepositoryLocation}, cloning from {RepositoryUrl}", configuration.RepositoryLocation, configuration.RepositoryUrl);

                try
                {
                    await _gitService.CloneRepositoryAsync(configuration.RepositoryUrl, configuration.RepositoryLocation);
                    _log.LogInformation("Repository cloned successfully to {RepositoryLocation}", configuration.RepositoryLocation);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to clone repository from {RepositoryUrl} to {RepositoryLocation}", configuration.RepositoryUrl, configuration.RepositoryLocation);
                    throw new InvalidOperationException($"Failed to clone repository: {ex.Message}", ex);
                }
            }
            else
            {
                _progressService.LogOperationProgress("Initializing Git repository", null, "Directory exists at {RepositoryLocation} but is not a Git repository, initializing", configuration.RepositoryLocation);
                    
                try
                {
                    await _gitService.InitializeRepositoryAsync(configuration.RepositoryLocation, configuration.RepositoryUrl);
                    _log.LogInformation("Git repository initialized successfully at {RepositoryLocation}", configuration.RepositoryLocation);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to initialize Git repository at {RepositoryLocation}", configuration.RepositoryLocation);
                    throw new InvalidOperationException($"Failed to initialize Git repository: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>
    /// Performs complete rollback with backup restoration
    /// </summary>
    private async Task PerformRollbackWithBackupAsync(
        List<SystemVersion> executedVersions, 
        BackupResult backup, 
        string[] composeFiles, 
        string workingDirectory)
    {
        _log.LogInformation("Starting rollback sequence");
        
        // Stop all services
        await _dockerComposeService.StopServicesAsync(composeFiles, workingDirectory);
        
        // Execute DOWN scripts in reverse order for successfully executed versions
        if (executedVersions.Any())
        {
            _log.LogInformation("Executing DOWN scripts for rollback: {Versions}", 
                string.Join(", ", executedVersions.Select(v => v.ToString())));
            
            var allScripts = await _scriptMigrationService.DiscoverScriptsAsync(workingDirectory);
            var downScripts = allScripts
                .Where(s => s.Direction == MigrationDirection.Down && executedVersions.Contains(s.Version))
                .OrderByDescending(s => s.Version) // Reverse order for rollback
                .ToList();
            
            if (downScripts.Any())
            {
                await _scriptMigrationService.ExecuteScriptsAsync(downScripts, workingDirectory);
                _log.LogInformation("DOWN scripts executed successfully");
            }
        }
        
        // Restore from backup
        _log.LogInformation("Restoring from backup: {BackupFile}", backup.BackupFilePath);
        var restoreResult = await _backupService.RestoreBackupAsync(workingDirectory, backup.BackupFilePath!);
        
        if (!restoreResult.Success)
        {
            _log.LogError("Backup restoration failed: {Error}", restoreResult.Error);
            throw new Exception($"Backup restoration failed: {restoreResult.Error}");
        }
        
        // Start original services
        _log.LogInformation("Starting original services after rollback");
        await _dockerComposeService.StartServicesAsync(composeFiles, workingDirectory);
        
        _log.LogInformation("Rollback sequence completed successfully");
    }

    /// <summary>
    /// Updates the deployment state with executed scripts
    /// </summary>
    private async Task UpdateDeploymentStateAsync(
        DeploymentState? currentState, 
        string newVersion, 
        List<SystemVersion> executedVersions, 
        string workingDirectory)
    {
        var updatedUp = (currentState?.Up ?? ImmutableSortedSet<SystemVersion>.Empty).Union(executedVersions);
        var deploymentState = new DeploymentState(newVersion, DateTime.Now)
        {
            Up = updatedUp,
            Failed = currentState?.Failed ?? ImmutableSortedSet<SystemVersion>.Empty
        };
        
        await _deploymentStateProvider.SaveDeploymentStateAsync(workingDirectory, deploymentState);
        _log.LogInformation("Deployment state updated to version {Version} with {ExecutedCount} executed scripts", 
            newVersion, executedVersions.Count);
    }

   
}