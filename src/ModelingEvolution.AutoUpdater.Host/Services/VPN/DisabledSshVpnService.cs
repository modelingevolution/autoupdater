namespace ModelingEvolution.AutoUpdater.Host.Services.VPN;

public class DisabledSshVpnService : ISshVpnService
{
    public Task<bool> IsVpnActiveAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task<bool> StartVpnAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task<bool> StopVpnAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task<VpnStatus> GetVpnStatusAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new VpnStatus(
            IsActive: false,
            InterfaceName: "wg0",
            LocalAddress: null,
            RemoteEndpoint: null,
            LastHandshake: null,
            BytesReceived: 0,
            BytesSent: 0,
            ErrorMessage: "SSH VPN is disabled in configuration"
        ));
    }
}