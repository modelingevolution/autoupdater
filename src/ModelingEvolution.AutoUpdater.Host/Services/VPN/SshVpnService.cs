using ModelingEvolution.AutoUpdater;
using System.Text.Json;
using ModelingEvolution.AutoUpdater.Extensions;
using ModelingEvolution.AutoUpdater.Services;

namespace ModelingEvolution.AutoUpdater.Host.Services.VPN;

public class SshVpnService : ISshVpnService
{
    private readonly ILogger<SshVpnService> _logger;
    private readonly ISshConnectionManager _ssh;
    private readonly SshVpnConfiguration _sshVpnConfig;
    

    public SshVpnService(
        ILogger<SshVpnService> logger,
        IConfiguration configuration, ISshConnectionManager ssh)
    {
        _logger = logger;
        _ssh = ssh;
        _sshVpnConfig = configuration.GetSection("SshVpn").Get<SshVpnConfiguration>() 
                        ?? new SshVpnConfiguration();
        _logger.LogInformation($"Vpn: start-script: {_sshVpnConfig.StartScript}, stop-script: {_sshVpnConfig.StopScript}, status: {_sshVpnConfig.StatusScript}");
    }

    public async Task<bool> IsVpnActiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ExecuteVpnCommandAsync("status", cancellationToken);
            if (!result.IsSuccess) return false;
            
            var status = JsonSerializer.Deserialize<VpnStatus>(result.Output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return status?.IsActive ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check VPN status");
            return false;
        }
    }

    public async Task<bool> StartVpnAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting VPN connection via SSH");
            
            var result = await ExecuteVpnCommandAsync("up", cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogError("Failed to start VPN: {Error}", result.Error);
                return false;
            }

            var response = JsonSerializer.Deserialize<VpnOp>(result.Output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            if (response?.Status != "success")
            {
                _logger.LogError("VPN start failed: {Message}", response?.Message ?? "Unknown error");
                return false;
            }

            _logger.LogInformation("VPN connection started successfully: {Message}", response.Message);
            return response.State == "active";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start VPN connection");
            return false;
        }
    }

    public async Task<bool> StopVpnAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Stopping VPN connection via SSH");
            
            var result = await ExecuteVpnCommandAsync("down", cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogError("Failed to stop VPN: {Error}", result.Error);
                return false;
            }

            var response = JsonSerializer.Deserialize<VpnOp>(result.Output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            if (response?.Status != "success")
            {
                _logger.LogError("VPN stop failed: {Message}", response?.Message ?? "Unknown error");
                return false;
            }

            _logger.LogInformation("VPN connection stopped successfully: {Message}", response.Message);
            return response.State == "inactive";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop VPN connection");
            return false;
        }
    }

    public async Task<VpnStatus> GetVpnStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ExecuteVpnCommandAsync("status", cancellationToken);
            
            if (!result.IsSuccess)
            {
                return new VpnStatus(
                    IsActive: false,
                    InterfaceName: _sshVpnConfig.InterfaceName,
                    LocalAddress: null,
                    RemoteEndpoint: null,
                    LastHandshake: null,
                    BytesReceived: 0,
                    BytesSent: 0,
                    ErrorMessage: result.Error,
                    Name: null
                );
            }

            var status = JsonSerializer.Deserialize<VpnStatus>(result.Output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            return status ?? new VpnStatus(
                IsActive: false,
                InterfaceName: _sshVpnConfig.InterfaceName,
                LocalAddress: null,
                RemoteEndpoint: null,
                LastHandshake: null,
                BytesReceived: 0,
                BytesSent: 0,
                ErrorMessage: "Failed to parse VPN status",
                Name: null
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get VPN status");
            return new VpnStatus(
                IsActive: false,
                InterfaceName: _sshVpnConfig.InterfaceName,
                LocalAddress: null,
                RemoteEndpoint: null,
                LastHandshake: null,
                BytesReceived: 0,
                BytesSent: 0,
                ErrorMessage: ex.Message,
                Name: null
            );
        }
    }

    private async Task<SshCommandResult> ExecuteVpnCommandAsync(string action, CancellationToken cancellationToken)
    {
        var commandPath = action switch
        {
            "up" => _sshVpnConfig.StartScript,
            "down" => _sshVpnConfig.StopScript,
            "status" => _sshVpnConfig.StatusScript,
            _ => throw new ArgumentException($"Unknown VPN action: {action}")
        };

        var command = $"sudo {commandPath}";
        
        _logger.LogDebug("Executing VPN command: {Command}", command);
        
        
        using var client = await _ssh.CreateSshServiceAsync();
        return await client.ExecuteCommandAsync(command);
    }

}

public enum VpnProviderAccess
{
    None,
    Ssh,
    NetworkManager
}

public class SshVpnConfiguration
{
    
    public string InterfaceName { get; set; } = "wg0";

    //TODO: The script should return status in JSON format schema is VpnOp (new class)
    public string StartScript { get; set; } = "/usr/local/bin/wg-up.sh";

    //TODO: The script should return status in JSON format schema is VpnOp (new class)
    public string StopScript { get; set; } = "/usr/local/bin/wg-down.sh";

    // The script should return status in JSON format schema is VpnStatus
    public string StatusScript { get; set; } = "/usr/local/bin/wg-status.sh";
}
