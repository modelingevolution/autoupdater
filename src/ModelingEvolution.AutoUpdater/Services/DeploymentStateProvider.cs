using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Handles deployment state persistence using JSON files
    /// </summary>
    public class DeploymentStateProvider : IDeploymentStateProvider
    {
        private readonly ISshService _sshService;
        private readonly ILogger<DeploymentStateProvider> _logger;
        private const string StateFileName = "deployment.state.json";

        public DeploymentStateProvider(ISshService sshService, ILogger<DeploymentStateProvider> logger)
        {
            _sshService = sshService ?? throw new ArgumentNullException(nameof(sshService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<DeploymentState?> GetDeploymentStateAsync(string deploymentPath)
        {
            try
            {
                _logger.LogDebug("Getting deployment state from {DeploymentPath}", deploymentPath);

                if (string.IsNullOrWhiteSpace(deploymentPath))
                {
                    throw new ArgumentException("Deployment path cannot be null or whitespace", nameof(deploymentPath));
                }

                var stateFilePath = Path.Combine(deploymentPath, StateFileName);
                
                if (!await _sshService.FileExistsAsync(stateFilePath))
                {
                    _logger.LogDebug("No deployment state file found at {StateFilePath}", stateFilePath);
                    return null;
                }

                var stateContent = await _sshService.ReadFileAsync(stateFilePath);
                
                if (string.IsNullOrWhiteSpace(stateContent))
                {
                    _logger.LogWarning("Deployment state file is empty at {StateFilePath}", stateFilePath);
                    return null;
                }

                var state = JsonSerializer.Deserialize<DeploymentState>(stateContent);
                
                _logger.LogDebug("Retrieved deployment state: Version={Version}, Updated={Updated}", 
                    state?.Version, state?.Updated);
                
                return state;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize deployment state from {DeploymentPath}", deploymentPath);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get deployment state from {DeploymentPath}", deploymentPath);
                return null;
            }
        }

        public async Task SaveDeploymentStateAsync(string deploymentPath, DeploymentState state)
        {
            try
            {
                _logger.LogDebug("Saving deployment state to {DeploymentPath}: Version={Version}, Updated={Updated}", 
                    deploymentPath, state?.Version, state?.Updated);

                if (string.IsNullOrWhiteSpace(deploymentPath))
                {
                    throw new ArgumentException("Deployment path cannot be null or whitespace", nameof(deploymentPath));
                }

                if (state == null)
                {
                    throw new ArgumentNullException(nameof(state));
                }

                // Ensure the deployment directory exists
                if (!await _sshService.DirectoryExistsAsync(deploymentPath))
                {
                    await _sshService.CreateDirectoryAsync(deploymentPath);
                }

                var stateFilePath = Path.Combine(deploymentPath, StateFileName);
                var stateContent = JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await _sshService.WriteFileAsync(stateFilePath, stateContent);
                
                _logger.LogInformation("Deployment state saved successfully to {StateFilePath}", stateFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save deployment state to {DeploymentPath}", deploymentPath);
                throw;
            }
        }

        public async Task<string?> GetCurrentVersionAsync(string deploymentPath)
        {
            try
            {
                var state = await GetDeploymentStateAsync(deploymentPath);
                return state?.Version;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get current version from {DeploymentPath}", deploymentPath);
                return null;
            }
        }

        public async Task<bool> DeploymentStateExistsAsync(string deploymentPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deploymentPath))
                {
                    return false;
                }

                var stateFilePath = Path.Combine(deploymentPath, StateFileName);
                return await _sshService.FileExistsAsync(stateFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check if deployment state exists at {DeploymentPath}", deploymentPath);
                return false;
            }
        }
    }
}