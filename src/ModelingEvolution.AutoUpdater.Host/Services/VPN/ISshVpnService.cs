namespace ModelingEvolution.AutoUpdater.Host.Services.VPN;

public interface ISshVpnService
{
    Task<bool> IsVpnActiveAsync(CancellationToken cancellationToken = default);
    Task<bool> StartVpnAsync(CancellationToken cancellationToken = default);
    Task<bool> StopVpnAsync(CancellationToken cancellationToken = default);
    Task<VpnStatus> GetVpnStatusAsync(CancellationToken cancellationToken = default);
}

public record VpnStatus(
    bool IsActive,
    string? InterfaceName,
    string? LocalAddress,
    string? RemoteEndpoint,
    string? LastHandshake,
    long BytesReceived,
    long BytesSent,
    string? ErrorMessage = null
);