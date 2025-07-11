using ModelingEvolution.AutoUpdater;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelingEvolution.AutoUpdater.Host.Services.VPN;

public class SshVpnService : ISshVpnService
{
    private readonly ILogger<SshVpnService> _logger;
    private readonly SshVpnConfiguration _sshVpnConfig;
    private readonly SshConfiguration _sshConfig;

    public SshVpnService(
        ILogger<SshVpnService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _sshVpnConfig = configuration.GetSection("SshVpn").Get<SshVpnConfiguration>() 
            ?? new SshVpnConfiguration();
        
        // Build SSH configuration from root settings
        var sshHost = configuration.GetValue<string>("SshHost");
        var sshUser = configuration.GetValue<string>("SshUser", "deploy");
        var authMethod = configuration.GetValue<string>("SshAuthMethod", "PrivateKey");
        var keyPath = configuration.GetValue<string>("SshKeyPath", "/data/ssh/id_rsa");
        
        if (string.IsNullOrEmpty(sshHost))
            throw new InvalidOperationException("SshHost configuration is required for SSH VPN");
        
        _sshConfig = new SshConfiguration
        {
            Host = sshHost,
            User = sshUser,
            AuthMethod = Enum.Parse<SshAuthMethod>(authMethod, true),
            KeyPath = keyPath,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<bool> IsVpnActiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ExecuteVpnCommandAsync("status", cancellationToken);
            if (!result.IsSuccess) return false;
            
            var statusResponse = ParseJsonResponse(result.Output);
            return statusResponse?.State == "active";
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

            var response = ParseJsonResponse(result.Output);
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

            var response = ParseJsonResponse(result.Output);
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
                    ErrorMessage: result.Error
                );
            }

            var response = ParseJsonResponse(result.Output);
            return ParseVpnStatus(response);
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
                ErrorMessage: ex.Message
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
        
        using var sshManager = new SshConnectionManager(_sshConfig, _logger);
        await sshManager.CreateConnectionAsync();
        return await sshManager.ExecuteCommandAsync(command);
    }

    private VpnJsonResponse? ParseJsonResponse(string jsonOutput)
    {
        try
        {
            return JsonSerializer.Deserialize<VpnJsonResponse>(jsonOutput, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON response: {Output}", jsonOutput);
            return null;
        }
    }

    private VpnStatus ParseVpnStatus(VpnJsonResponse? response)
    {
        if (response == null)
        {
            return new VpnStatus(
                IsActive: false,
                InterfaceName: _sshVpnConfig.InterfaceName,
                LocalAddress: null,
                RemoteEndpoint: null,
                LastHandshake: null,
                BytesReceived: 0,
                BytesSent: 0,
                ErrorMessage: "Failed to parse JSON response"
            );
        }

        var isActive = response.State == "active";
        var interfaceName = _sshVpnConfig.InterfaceName;
        
        string? localAddress = null;
        string? remoteEndpoint = null;
        string? lastHandshake = null;
        long bytesReceived = 0;
        long bytesSent = 0;

        try
        {
            // Parse local address from interface info
            if (!string.IsNullOrEmpty(response.Interface))
            {
                var addressMatch = Regex.Match(response.Interface, @"inet\s+([0-9./]+)");
                if (addressMatch.Success)
                {
                    localAddress = addressMatch.Groups[1].Value;
                }
            }

            // Parse details for WireGuard info
            if (!string.IsNullOrEmpty(response.Details))
            {
                // Parse remote endpoint
                var endpointMatch = Regex.Match(response.Details, @"endpoint:\s+([0-9.:]+)");
                if (endpointMatch.Success)
                {
                    remoteEndpoint = endpointMatch.Groups[1].Value;
                }

                // Parse last handshake
                var handshakeMatch = Regex.Match(response.Details, @"latest handshake:\s+(.+?)(?:\n|$)");
                if (handshakeMatch.Success)
                {
                    var handshakeStr = handshakeMatch.Groups[1].Value.Trim();
                    if (!handshakeStr.Contains("(none)") && !handshakeStr.Contains("never"))
                    {
                        lastHandshake = handshakeStr;
                    }
                }

                // Parse transfer statistics
                var transferMatch = Regex.Match(response.Details, @"transfer:\s+([0-9.]+\s*[KMGT]?B)\s+received,\s+([0-9.]+\s*[KMGT]?B)\s+sent");
                if (transferMatch.Success)
                {
                    bytesReceived = ParseBytes(transferMatch.Groups[1].Value);
                    bytesSent = ParseBytes(transferMatch.Groups[2].Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse VPN status details");
        }

        return new VpnStatus(
            IsActive: isActive,
            InterfaceName: interfaceName,
            LocalAddress: localAddress,
            RemoteEndpoint: remoteEndpoint,
            LastHandshake: lastHandshake,
            BytesReceived: bytesReceived,
            BytesSent: bytesSent,
            ErrorMessage: response.Status == "error" ? response.Message : null
        );
    }

    private static long ParseBytes(string bytesStr)
    {
        var match = Regex.Match(bytesStr.Trim(), @"([0-9.]+)\s*([KMGT]?)B?");
        if (!match.Success) return 0;

        if (!double.TryParse(match.Groups[1].Value, out var value)) return 0;

        var unit = match.Groups[2].Value.ToUpperInvariant();
        var multiplier = unit switch
        {
            "K" => 1024L,
            "M" => 1024L * 1024L,
            "G" => 1024L * 1024L * 1024L,
            "T" => 1024L * 1024L * 1024L * 1024L,
            _ => 1L
        };

        return (long)(value * multiplier);
    }
}

public enum VpnProviderAccess
{
    None,
    Ssh,
    NetworkManager
}

public enum VpnProvider
{
    None,
    Wireguard
}

public class SshVpnConfiguration
{
    public string InterfaceName { get; set; } = "wg0";
    public string StartScript { get; set; } = "/usr/local/bin/wg-up.sh";
    public string StopScript { get; set; } = "/usr/local/bin/wg-down.sh";
    public string StatusScript { get; set; } = "/usr/local/bin/wg-status.sh";
}

public class VpnJsonResponse
{
    public string Status { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? Details { get; set; }
    public string? Routes { get; set; }
    public string? Connectivity { get; set; }
    public string? Interface { get; set; }
}