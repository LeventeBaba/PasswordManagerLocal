using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

/// <summary>Authenticated historical ingress; the runtime serializes its owner with active networking.</summary>
internal sealed class PassiveDiscoveryHostedService(LocalDiscoveryHostedService discovery, TcpSyncServerHostedService tcp,
    SyncNetworkRefreshHostedService network) : IPassiveSyncRuntime
{
    public bool IsHealthy => tcp.IsHealthy && discovery.IsHealthy && network.IsHealthy;
    public bool RequiresProcessRestart => tcp.RequiresProcessRestart || discovery.RequiresProcessRestart || network.RequiresProcessRestart;

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Partial startup is cleaned up by the same serialized runtime owner. Do not
        // hide a failed cleanup behind a second local startup attempt.
        await tcp.StartPassiveAsync(ct);
        await discovery.StartPassiveAsync(ct);
        await network.StartPassiveAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var failures = new List<Exception>();
        try { await network.StopPassiveAsync(ct); } catch (Exception ex) { failures.Add(ex); }
        try { await discovery.StopPassiveAsync(ct); } catch (Exception ex) { failures.Add(ex); }
        try { await tcp.StopPassiveAsync(ct); } catch (Exception ex) { failures.Add(ex); }
        if (failures.Count != 0) throw new AggregateException("Passive synchronization ingress did not stop safely.", failures);
    }
}
