using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

/// <summary>Backend-owned passive control ingress; active discovery queries retain their existing runtime owner.</summary>
internal sealed class PassiveDiscoveryHostedService(LocalDiscoveryHostedService discovery, TcpSyncServerHostedService tcp,
    SyncNetworkRefreshHostedService network) : IBackendHostedService
{
    public async Task StartAsync(CancellationToken ct = default)
    {
        await tcp.StartPassiveAsync(ct);
        try
        {
            await discovery.StartPassiveAsync(ct);
            await network.StartPassiveAsync(ct);
        }
        catch
        {
            await network.StopPassiveAsync(CancellationToken.None);
            try { await discovery.StopPassiveAsync(CancellationToken.None); }
            finally { await tcp.StopPassiveAsync(CancellationToken.None); }
            throw;
        }
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        await network.StopPassiveAsync(ct);
        try { await discovery.StopPassiveAsync(ct); }
        finally { await tcp.StopPassiveAsync(ct); }
    }
}
